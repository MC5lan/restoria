// Anim.cs — 轻量动效引擎（全程序共用一个 16ms 定时器）
// 设计原则（生产级约束）：
//   * 只重绘参与动画的控件（回调里自己 Invalidate），绝不整窗刷新；
//   * 同一控件同时只允许一个动画通道，新动画顶掉旧动画，不会叠加抖动；
//   * 控件销毁/句柄未建时自动丢弃，杜绝动画回调打在死对象上；
//   * 帧率交由系统定时器（约 15.6ms），老机器上等价于 60fps 上限、实际按机器能力降级。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PicTool
{
    enum AnimEase { Linear, Out, OutBack }

    // .NET 2.0 没有无参 System.Action / 泛型 Action<T>，这里定义专用委托
    public delegate void AnimApply(double value);
    public delegate void AnimDone();

    static class Anim
    {
        static Timer timer;
        static readonly List<Runner> runners = new List<Runner>();

        class Runner
        {
            public Control Target;
            public int StartTick, Duration;
            public double From, To;
            public AnimEase Ease;
            public AnimApply Apply;         // 参数：插值后的当前值
            public AnimDone Done;
        }

        // 把 target 上的一个值从 from 过渡到 to，每帧调用 apply(当前值)
        public static void Run(Control target, double from, double to, int ms,
                               AnimEase ease, AnimApply apply, AnimDone done)
        {
            if (target == null || target.IsDisposed || apply == null) return;
            if (ms <= 0 || Math.Abs(to - from) < 1e-9)
            {
                apply(to);
                if (done != null) done();
                return;
            }
            Stop(target);
            if (!target.IsHandleCreated)
            {
                // 句柄还没建（窗体构造期）：直接落终值，不排队
                apply(to);
                if (done != null) done();
                return;
            }
            Runner r = new Runner();
            r.Target = target; r.Duration = ms; r.From = from; r.To = to;
            r.Ease = ease; r.Apply = apply; r.Done = done;
            r.StartTick = Environment.TickCount;
            runners.Add(r);
            EnsureTimer();
            if (!timer.Enabled) timer.Start();
        }

        public static void Stop(Control target)
        {
            for (int i = runners.Count - 1; i >= 0; i--)
                if (runners[i].Target == target) runners.RemoveAt(i);
        }

        public static double Ease(double t, AnimEase e)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            if (e == AnimEase.Out) t = 1 - (1 - t) * (1 - t) * (1 - t);                 // cubic-out
            else if (e == AnimEase.OutBack)
            {
                double s = 1.35;
                double u = t - 1;
                t = 1 + u * u * ((s + 1) * u + s);                                       // 微回弹，用于小位移
            }
            return t;
        }

        static void EnsureTimer()
        {
            if (timer != null) return;
            timer = new Timer();
            timer.Interval = 16;
            timer.Tick += delegate { Step(); };
        }

        static void Step()
        {
            if (runners.Count == 0) { timer.Stop(); return; }
            int now = Environment.TickCount;
            List<Runner> finished = null;
            for (int i = runners.Count - 1; i >= 0; i--)
            {
                Runner r = runners[i];
                if (r.Target == null || r.Target.IsDisposed || !r.Target.IsHandleCreated)
                {
                    runners.RemoveAt(i);
                    continue;
                }
                double t = (now - r.StartTick) / (double)r.Duration;
                if (t >= 1) t = 1;
                try { r.Apply(r.From + (r.To - r.From) * Ease(t, r.Ease)); }
                catch { runners.RemoveAt(i); continue; }
                if (t >= 1)
                {
                    runners.RemoveAt(i);
                    if (r.Done != null)
                    {
                        if (finished == null) finished = new List<Runner>();
                        finished.Add(r);
                    }
                }
            }
            if (runners.Count == 0) timer.Stop();
            if (finished != null)
                for (int i = 0; i < finished.Count; i++) finished[i].Done();
        }
    }

    // 颜色插值（hover 渐变等处共用）
    static class ColorLerp
    {
        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }
    }
}
