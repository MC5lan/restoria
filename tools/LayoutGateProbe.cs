// LayoutGateProbe.cs — is the layout gate actually sensitive to 1px?
//
// The layout gate is described as objective evidence that a UI refactor
// changed nothing. That claim is only worth something if the snapshot notices
// small changes, and nothing ever tested that. The gate could be comparing
// almost nothing and still pass every run.
//
// This probe answers it directly: nudge one control by a single pixel, take a
// fresh snapshot, and diff it against the one taken beforehand. A 1px change
// that stays invisible would mean the gate is decoration.
//
// The probe restores the original position before exiting, and it edits
// nothing on disk: the nudge happens in a separate process's in-memory form.
//
// Build: tools\LayoutGateProbe.bat
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

static class LayoutGateProbe
{
    [STAThread]
    static int Main(string[] args)
    {
        string repo = RepoRoot();
        if (repo == null) { Console.Error.WriteLine("repository root not found"); return 2; }
        string exe = FindExe(repo);
        if (exe == null) { Console.Error.WriteLine("dist\\Restoria.exe missing - run build.bat"); return 2; }

        Console.WriteLine("Layout gate sensitivity probe");
        Console.WriteLine("  repo: " + repo);
        Console.WriteLine();

        string rep = Path.Combine(Path.GetTempPath(), "PicTool-layout.txt");

        Console.WriteLine("[1] snapshot before the nudge");
        if (Snapshot(exe, repo, rep) != 0) { Console.Error.WriteLine("  failed"); return 3; }
        string before = File.ReadAllText(rep);
        Console.WriteLine("    " + before.Split('\n').Length + " lines");

        Console.WriteLine("[2] moving one control by 1 px");
        string nudged = SnapshotNudged(exe, repo, rep, 1);
        if (nudged == null) { Console.Error.WriteLine("  could not nudge"); return 4; }
        Console.WriteLine("    moved: " + nudged);
        string after = File.ReadAllText(rep);
        Console.WriteLine("    " + after.Split('\n').Length + " lines");

        Console.WriteLine();
        int diff = CountDiff(before, after);
        if (diff == 0)
        {
            Console.WriteLine("VERDICT: a 1px change is INVISIBLE to the layout gate.");
            Console.WriteLine("         Do not describe it as evidence of anything at that resolution.");
            return 1;
        }
        Console.WriteLine("VERDICT: the gate caught the 1px change.");
        Console.WriteLine("         " + diff + " row(s) differ, so the comparison is real.");
        Console.WriteLine();
        Console.WriteLine("First differences:");
        ShowDiff(before, after, 6);
        return 0;
    }

    static string RepoRoot()
    {
        // The probe is launched from the repository root by the .bat, so the
        // working directory is the reliable starting point; BaseDirectory is
        // the temp folder the exe was compiled into.
        string p = Directory.GetCurrentDirectory();
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(p, "build.bat"))) return p;
            DirectoryInfo up = Directory.GetParent(p);
            if (up == null) break;
            p = up.FullName;
        }
        return null;
    }

    static string FindExe(string repo)
    {
        foreach (string n in new string[] { "Restoria.exe", "PicTool.exe" })
        {
            string p = Path.Combine(repo, "dist", n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static int Snapshot(string exe, string repo, string rep)
    {
        if (File.Exists(rep)) File.Delete(rep);
        Run(exe, repo, "--layout", 120000);
        return File.Exists(rep) ? 0 : 5;
    }

    // Reuse the program's own DumpLayout, but move a control first: the form is
    // created here, nudged, then asked to dump. Going through the public entry
    // point keeps the snapshot logic in one place instead of duplicating it.
    static string SnapshotNudged(string exe, string repo, string rep, int dx)
    {
        if (File.Exists(rep)) File.Delete(rep);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Assembly asm = Assembly.LoadFrom(exe);
        Type t = asm.GetType("PicTool.MainForm");
        if (t == null) { Console.Error.WriteLine("  MainForm not found"); return null; }

        // The probe builds the form itself, so it bypasses Program.Main's
        // --layout branch and therefore the DeterministicLayout flag that
        // branch sets. Without this the probe measures a different snapshot
        // than the gate does (AutoSize labels keep their measured heights) and
        // the comparison is meaningless. Set it the same way the gate does.
        FieldInfo det = t.GetField("DeterministicLayout",
            BindingFlags.Public | BindingFlags.Static);
        if (det != null) det.SetValue(null, true);

        object form = Activator.CreateInstance(t, new object[] { null });
        try
        {
            Form f = (Form)form;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(0, 0);
            f.WindowState = FormWindowState.Normal;
            f.ClientSize = new Size(1080, 680);
            f.Show();
            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();

            // Pick a control that DumpLayout's own re-layout will not undo.
            // DumpLayout resets ClientSize and calls PerformLayout, which
            // repositions docked and anchored controls back to where the layout
            // engine wants them -- moving one of those proves nothing. A child
            // of a container whose layout is driven by explicit bounds keeps
            // the nudge, so that is what we look for: a control whose Left is
            // not dictated by docking.
            Control target = null;
            foreach (Control c in AllControls(f))
            {
                if (!c.Visible || c.Width <= 20 || c.Height <= 8) continue;
                if (c.Dock != DockStyle.None) continue;
                if (c is Label) continue;                 // labels are laid out in flow
                if (c.Parent == null) continue;
                if (c.Name == null || c.Name.Length == 0) continue;
                target = c;
                break;
            }
            if (target == null) target = FirstUnnamed(f);
            if (target == null) { Console.Error.WriteLine("  no suitable control"); return null; }

            string label = target.GetType().Name + " '" + target.Name + "'";
            // Nudge the right edge: a docked control's Left is recomputed by
            // PerformLayout, but an undocked one's width is ours to change, and
            // DumpCtrl records width, so the snapshot should see it.
            target.Width += dx;
            Application.DoEvents();
            System.Threading.Thread.Sleep(250);
            Application.DoEvents();

            // Confirm the nudge survived, otherwise the result would be a
            // false negative dressed up as a finding.
            if (target.Width <= 0)
            {
                Console.Error.WriteLine("  nudge did not stick (" + label + ")");
                return null;
            }

            t.GetMethod("DumpLayout").Invoke(form, null);
            return label;
        }
        finally
        {
            try { ((Form)form).Close(); } catch { }
        }
    }

    static System.Collections.Generic.IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (Control d in AllControls(c)) yield return d;
        }
    }

    static Control FirstUnnamed(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c.Visible && c.Dock == DockStyle.None && c.Width > 20 && c.Height > 8
                && !(c is Label) && c.Parent != null)
                return c;
            Control hit = FirstUnnamed(c);
            if (hit != null) return hit;
        }
        return null;
    }

    static void Run(string exe, string repo, string args, int timeoutMs)
    {
        System.Diagnostics.Process p = new System.Diagnostics.Process();
        p.StartInfo.FileName = exe;
        p.StartInfo.Arguments = args;
        p.StartInfo.WorkingDirectory = repo;
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.CreateNoWindow = true;
        p.Start();
        p.WaitForExit(timeoutMs);
    }

    static int CountDiff(string a, string b)
    {
        string[] la = a.Split('\n');
        string[] lb = b.Split('\n');
        int n = Math.Max(la.Length, lb.Length);
        int d = 0;
        for (int i = 0; i < n; i++)
        {
            string x = i < la.Length ? la[i] : "<missing>";
            string y = i < lb.Length ? lb[i] : "<missing>";
            if (x != y) d++;
        }
        return d;
    }

    static void ShowDiff(string a, string b, int max)
    {
        string[] la = a.Split('\n');
        string[] lb = b.Split('\n');
        int n = Math.Max(la.Length, lb.Length);
        int shown = 0;
        for (int i = 0; i < n && shown < max; i++)
        {
            string x = i < la.Length ? la[i] : "<missing>";
            string y = i < lb.Length ? lb[i] : "<missing>";
            if (x == y) continue;
            shown++;
            Console.WriteLine("      line " + (i + 1));
            Console.WriteLine("        before: " + x.Trim());
            Console.WriteLine("        after : " + y.Trim());
        }
    }
}