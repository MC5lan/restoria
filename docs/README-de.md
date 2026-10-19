# Restoria

Macht aus gescannten Archivseiten saubere A4-Ablage-Dokumente — auch auf Rechnern von der Güte eines Windows 7.

Ausrichtungserkennung, Schräglaufkorrektur, Entrauschen, Fleckenentfernung, Schwarz-Weiß-Werte und Tonwertanpassung, dann A4-Ausgabe. Ein Klick erledigt, was eine Scanoperatorin sonst einen halben Tag kostet: Seite drehen, beschneiden, Punkte herausputzen, geradeziehen, Satz spiegeln.

**Eine Datei. Doppelklick startet. Nichts verlässt den Rechner.**

- **Läuft auf:** Windows 7 SP1 bis Windows 11 (.NET Framework 4.8, AnyCPU)
- **Oberfläche:** vollständig mit GDI+ gezeichnet und doppelt gepuffert, damit ältere Hardware nicht ausbremst; helles und dunkles Design
- **Code:** rund 26.000 Zeilen C#, keine Fremd-NuGet-Pakete, baut mit dem Compiler, der unter Windows mitgeliefert wird

---

## Download

Die Releases stehen auf der [Releases-Seite](https://github.com/MC5lan/restoria/releases).

| Datei | Inhalt |
|---|---|
| `Restoria-setup.exe` | Einzeldatei-Installer. Doppelklick, Lizenz akzeptieren, fertig |
| `Restoria-portable.zip` | Ohne Installation. Auspacken, `setup-boot.exe` starten |

Beide Pakete enthalten die optionalen KI-Dateien und eine Win7-Kompatibilitätsebene. Der Installer prüft die .NET-Laufzeit und verifiziert vor dem Abschluss, dass die KI-Komponenten geladen werden können.

> **Noch nicht signiert.** Da die Binärdateien kein Code-Signaturzertifikat tragen, zeigt Windows eine Herausgeberwarnung. Wählen Sie *Weitere Informationen → Trotzdem ausführen*.

**Aus dem Quellcode:** `build.bat` erzeugt `dist\Restoria.exe`. Für die KI-Funktionen muss zusätzlich der Ordner `ai\` daneben kopiert werden (siehe [KI-Funktionen aktivieren](#ki-funktionen-aktivieren)).

---

## Weitere Sprachen

[English](../README.md) · [中文](README-zh.md) · [日本語](README-ja.md) · Français · [Español](README-es.md) · [Русский](README-ru.md)

---

## Funktionen

### Die Ein-Klick-Pipeline

Ausrichtung → Spiegelungserkennung → Entrauschen → Schräglaufkorrektur → Schwarz-Weiß-Werte und automatischer Tonwert → A4-Ausgabe.

- **Standard** — ausgewogen zwischen Tempo und Sauberkeit. Die Ausrichtung stützt sich ausschließlich auf Lochbohrungen am Bindungsrand.
- **Gründlich** — entfernt nur die eindeutigsten Staubpunkte und nimmt sich zurück, wenn Zeichen angekratzt würden. **Lieber bleibt ein Fleck, als dass ein Handschriftstrich verloren geht.** Seiten ohne Lochbohrungen werden per OCR ausgerichtet. Für blasse Handschriften und Bestände, bei denen Inhalt wichtiger ist als Geschwindigkeit.

Die Archivierung legt **ausschließlich neue Dateien an**; das Ausgangsbild wird nicht ein einziges Byte verändert. Ein abgebrochener Lauf setzt fort, wo er stehen blieb. Jeder Stapel erzeugt einen Bericht mit Seitenmaßen, Drehwinkel und Begründung sowie Zählungen und Zeitangaben.

### Bearbeiten einzelner Seiten

| Reiter | Funktion |
|---|---|
| Entfernen | Pinsel oder Rechteck über Schadstellen; fünf Füllmodi: einfach, verstärkt, gemischt, Papierrekonstruktion, KI |
| Zuschneiden | Manueller Beschnitt oder Einrasten auf ein A4-Brett |
| Ausrichtung | Manuelles Drehen oder Schräglaufkorrektur per Klick (Projektionsverfahren, ±20°) |
| Anpassen | Helligkeit, Kontrast, Sättigung, Farbtemperatur, Tonwerte, Gamma, Unscharfmaske (jeder Wert per Klick eingebbar) |
| Stempel | Verblasste rote Siegel auffrischen |
| Stapel | Feste Parameter auf einen ganzen Ordner anwenden |
| Scannen | Direkt mit einem WIA-Scanner sprechen; Ausgabeformat wählbar (JPG/PNG/TIF) |
| Dateien | Massenumbenennung, Ordnerverwaltung |
| Text | Anmerkungen auf die Seite setzen |

### Optionale KI-Inpainting (MI-GAN)

- ONNX Runtime 1.14.1 mit dem MI-GAN-Modell; verlängert lange gerade Strukturen wie Tabellenlinien über beschädigte Bereiche hinweg
- **Läuft in einem Kindprozess, nie im Hauptprozess.** Lässt sich die Laufzeit nicht laden, schalten sich die KI-Funktionen ab, alles andere läuft unverändert weiter
- Win7-Unterstützung über umbenannte Import-Tabellen-Shims (Details in den Kommentaren in `src/Ai/AiFill.cs`)

Das gesamte `ai/`-Paket umfasst 30 Dateien, rund 59 MB, und wird ausschließlich für KI-Inpainting und Stempelerkennung gebraucht.

### Technische Absicherung

- **134 Tests ohne Fenster** (`--selftest`): Algorithmus-Schwellwerte, Undo-Budget, Wiederherstellungsort, Tastenkonflikte
- **Layout-Baseline-Regression** (`--layout`): objektiver Beleg, dass ein UI-Umbau nichts verändert hat
- **Vier Tore** (`tools/gate.py`): Selbsttest, Layout-Baseline, KI-Artefakt, Leistungs-Rauchtest
- **Continuous Integration**: GitHub Actions baut und durchläuft die Tore bei jedem Push
- Parallelitäts-Regression für die Archiv-Pipeline: der gründliche Modus liefert bei parallel=1/2/4 byteweise identische Ergebnisse

---

## Bekannte Einschränkungen

Lesen Sie das, bevor Sie darauf vertrauen, dass das Werkzeug Ihr Material verarbeitet.

- **Die Ausrichtung braucht einen physikalischen Beleg.** Die Entscheidung über 180° stützt sich auf Lochbohrungen. Eine Seite ohne Lochbohrung — ein gebundenes Heft, ein einzelnes Blatt, ein zweiseitig gelochtes Dokument — wird **nicht** automatisch gedreht. Das ist Absicht: Eine bereits aufrecht stehende Tabellenseite zu drehen ist schlimmer, als ein umgekehrtes Blatt von einem Menschen bemerken zu lassen. Nutzen Sie für solche Seiten die manuelle Drehung oder den gründlichen Modus, der auf OCR zurückfällt.
- **Für Flachbettscanner-Ausgabe entworfen.** Handyfotos haben Perspektivverzerrungen und werden nicht verarbeitet.
- **Die Ausrichtungsschwellen sind auf etwa 300 DPI kalibriert.** Bei 600 DPI liegt der Lochbeleg außerhalb des kalibrierten Fensters, und das Werkzeug fällt auf „keine Drehung" zurück.
- **Die KI-Laufzeit ist auf ONNX Runtime 1.14.1 festgelegt**, die letzte Version, die unter Windows 7 lädt. Prüfen Sie vor einem Modelltausch die Operator-Kompatibilität mit `python tools\onnx_ops.py --diff ai\migan.onnx candidate.onnx`. (LaMa wurde verworfen, weil dessen FFT-Operator in den CPU-Kernels dieser Version fehlerhaft arbeitet.)
- **Die Archivierung verweigert das Bearbeiten von** Desktop, C:-Wurzel, Systemordnern und dem Benutzerprofil.
- **Noch kein Code-Signaturzertifikat**, daher zeigt Windows beim ersten Start eine Herausgeberwarnung.

---

## Aus dem Quellcode bauen

**Voraussetzungen:** Windows mit .NET Framework 4.x. Sonst nichts — kein Visual Studio, kein NuGet-Restore.

```bat
build.bat
```

Ergebnis ist `dist\Restoria.exe`. Das Build-Skript:

1. Prüft auf `vendor\onnxruntime_c_api.h`. Vorhanden erzeugt es die Interop-Schicht (`src\Ai\OrtApi.Generated.cs`) und baut mit KI-Unterstützung; fehlt die Datei, wird ohne KI gebaut, alles andere funktioniert unverändert.
2. Bettet das Symbol ein (`assets\app.ico`, von `tools\IconGen.cs` als GDI+-Pfadgeometrie gezeichnet — der Quellcode ist die einzige Quelle des Designs) und das Manifest (`assets\app.manifest`, das Win7–11-Unterstützung und `asInvoker` deklariert, sodass nie erhöhte Rechte angefordert werden).
3. Schreibt eine `.config`, die die .NET-4.x-Laufzeit deklariert.
4. Erzeugt eine PDB neben der EXE und benennt sie passend zum Produkt um, damit ein Absturzbericht von einem Gerät vor Ort gegen passende Symbole aufgelöst werden kann.

> **Zum Produktnamen:** `build.bat` kompiliert unter dem internen Namen `dist\PicTool.exe` und kopiert die Datei dann unter dem veröffentlichten Namen `dist\Restoria.exe` (aus `appname.txt`). Beide Dateien sind byteweise identisch. `PicTool` ist die interne Kennung und der Assembly-Namensraum; überall, wo Nutzer den Namen sehen, steht Restoria.

### KI-Funktionen aktivieren

Kopieren Sie den Ordner `ai\` neben die EXE:

```bat
xcopy /e /y ai dist\ai
```

Darin liegen `onnxruntime.dll`, `migan.onnx`, die Modelle in `ocr\` und die Shims in `win7\` — 30 Dateien, rund 59 MB. Wenn Sie sie nicht brauchen, löschen Sie `ai\` vor dem Bauen.

### Prüfungen ausführen

```bat
build.bat                                  :: bauen
dist\Restoria.exe --selftest               :: 1. Algorithmus-Selbsttest (Exitcode 0 = bestanden)
dist\Restoria.exe --layout                 :: 2. Layout-Schnappschuss (gegen die Baseline vergleichen)
python tools\check_resources.py --strict     :: 3. Ressourcen-Tor (LockBits-Paarung)
python tools\gate.py                       :: 4. alle Tore, inklusive Leistungs-Rauchtest
```

Die Layout-Baseline liegt in `tools\golden-layout.txt`. Nach einer beabsichtigten UI-Änderung erzeugen Sie sie mit `--regen-layout` neu.

Die Tore für KI-Artefakt und Leistung benötigen die feste Testseite `_probe\doc_test.png`, die `Restoria.exe --make-fixture` deterministisch erzeugt. Sie liegt im Repository, eine frische Kopie enthält sie also; fehlt sie, erzeugt das Tor sie neu und schlägt nur dann fehl, wenn schon die Erzeugung scheitert.

Die Leistungs-Baseline merkt sich eine Maschinenkennung. Eine Baseline von einem anderen Rechner wird gemeldet statt still verglichen — bauen Sie sie lokal mit `--regen-perf` neu auf.

---

## Kommandozeilen-Diagnose

Modi ohne Fenster für Skripte und Fehlerberichte. Berichte landen unter `%TEMP%\PicTool-*.txt`, Exit-Codes sind sauber, es erscheinen keine Dialoge.

| Schalter | Zweck |
|---|---|
| `--selftest` | 134 Algorithmus- und Interaktionstests; Exitcode 0 = alle bestanden |
| `--layout` | Layout-Schnappschuss (nach einem Umbau zeilengleich) |
| `--archive <ein> <aus> [dpi] [optionen]` | Pipeline ohne Fenster ausführen |
| `--skew-test <Datei oder Ordner> [n]` | Schräglauferkennung mit Kontrollvergleich am schwarzen Rand |
| `--ai-check` / `--ai-check-silent` | KI-Laufzeit und Modelle prüfen (silent für unbeaufsichtigte Installation) |
| `--ai-fill <ein> <aus> [x y w h]` | Eine Region per KI auf die Platte reparieren, für Offline-Vergleiche |
| `--paper-fill <ein> <aus> [x y w h]` | Papierbereich rekonstruieren und ablegen |
| `--make-fixture [pfad]` | Die deterministische Testseite der Tore exportieren |
| `--bench [golden] [--regen]` | Zeitmessung je Operator plus Ergebnis-Fingerabdrücke |
| `--wheeltest[=ordner]` | Mausrad-Routing: Arbeitsfläche, linke und rechte Leiste |
| `--uityping` | Fokusentscheidung der Eingabefelder, altes und neues Kriterium nebeneinander |
| `--undotest` | Undo-Budget und Rollback auf Frame-Ebene |
| `--cursortest` | Form des eigenen Cursors prüfen, hinterlässt einen Screenshot |
| `--droptest` | Empfangsbaum für Ziehen und Ablegen prüfen |

Umgebungsschalter: `PICTOOL_AI_TRACE=1`, `PICTOOL_OCR_TRACE=1`, `PICTOOL_SKEW_NOGUARD=1` sowie `PICTOOL_RECOVERY_ROOT` (verlegt den Wiederherstellungsbereich; die Prüfmodi nutzen das, damit sie niemals echte Benutzerdaten anfassen).

---

## Aufbau

```
├── src/                  Gesamter Quellcode (68 .cs-Dateien, rund 26.000 Zeilen)
│   ├── Program.cs        Einstiegspunkt, globale Ausnahmebehandlung, Diagnose-Verteilung
│   ├── Core/             Selbsttests, Benchmark, Version, Wiederherstellung, Bitmap-Brücke
│   ├── Platform/         Systemerkundung, WIA-Scannen, Dateiwerkzeuge
│   ├── Imaging/          Reine Bildalgorithmen (ohne UI-Abhängigkeit): Reinigung, Verstärkung, Reparatur, Archivierung
│   ├── Ai/               Optionale KI-Schicht: ONNX-Interop, MI-GAN, OCR-Ausrichtungsrichter
│   └── Ui/               Oberfläche und Interaktion (die einzige Stelle mit WinForms)
├── assets/               Symbole und Manifeste. Das Symbol wird von tools\IconGen.cs
│                         als GDI+-Pfadgeometrie gezeichnet — der Quellcode ist die einzige Designquelle
├── _probe/               Feste Testseite der Tore (neu erzeugbar mit --make-fixture)
├── tools/                Build- und Prüfwerkzeugkette
│   ├── gate.py           Die vier Tore: Selbsttest + Layout + KI-Artefakt + Leistung
│   ├── check_resources.py Ressourcen-Tor (LockBits-Paarung)
│   ├── onnx_ops.py       Scan der ONNX-Operator-Kompatibilität (vor Modellwechsel ausführen)
│   ├── archive_demo.py   Erzeugt Vorher-Nachher-Vergleichsbilder
│   ├── make_package.ps1  Paketkette (bauen → zusammensetzen → signieren → selbst prüfen)
│   └── install/          Quellcode von Installer und Deinstaller
├── ai/                   Optionales KI-Paket: ONNX-Laufzeit + MI-GAN + OCR-Modelle + Win7-Shims
├── vendor/               onnxruntime_c_api.h (erzeugt die Interop-Schicht)
├── docs/                 Übersetzungen und Architekturnotizen
├── build.bat             Bau mit einem Befehl
├── CHANGELOG.md          Versionsgeschichte
└── THIRD_PARTY_NOTICES   Lizenzen aller mitgelieferten Fremdkomponenten
```

Abhängigkeiten fließen in eine Richtung; Rückreferenzen sind nicht erlaubt:

```
Ui ──→ Imaging / Ai / Platform / Core
Ai ──→ Platform (SysInfo)
Imaging ──→ Platform (wenige)
Platform ──→ (nichts)
```

`Imaging` und `Ai` dürfen `System.Windows.Forms` nicht erwähnen; Algorithmen nehmen Parameter entgegen und liefern neue Bitmaps zurück, statt globalen Zustand zu verändern.

---

## Fremdkomponenten und Lizenzen

Der eigene Code von Restoria steht unter der MIT-Lizenz (siehe [LICENSE](../LICENSE)). Die folgenden Komponenten sind im Release enthalten und bleiben unter ihren eigenen Lizenzen:

| Komponente | Version | Urheberrecht | Lizenz |
|---|---|---|---|
| ONNX Runtime (`ai\onnxruntime.dll`, `ai\win7\onnxruntime.dll`) | 1.14.1 | © Microsoft Corporation | MIT |
| PP-OCRv3-Erkennungs- und Lesemodelle samt Wörterbuch (`ai\ocr\`) | PP-OCRv3 | © PaddlePaddle Authors | Apache-2.0 |
| MI-GAN-Inpainting-Modell (`ai\migan.onnx`) | 512, ICCV 2023 | © 2024 Picsart AI Research | MIT |
| Win7-API-Set-Kompatibilitäts-Shims (`ai\win7\*.dll`) | — | Microsoft-Weiterleitungen | MIT |

Vollständige Lizenztexte und Einzelheiten stehen in **[THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES)**.

Diese Komponenten dienen **ausschließlich** dem optionalen KI-Inpainting und der Stempelerkennung. Die Kernpipeline — Ausrichtung, Schräglauf, Entrauschen, Fleckenentfernung, Schwarz-Weiß-Werte, Tonwert, A4-Ausgabe — ist reines C# ohne native Abhängigkeit und läuft vollständig ohne den Ordner `ai\`. Das Programm prüft in einem Kindprozess, ob die KI-Laufzeit geladen werden kann, und schaltet diese Funktionen andernfalls sauber ab; kein KI-Codepfad läuft je im Hauptprozess.

## Danksagungen

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) — plattformübergreifende Inferenz-Engine
- [MI-GAN](https://github.com/Picsart-AI-Research/MI-GAN) — Bildinpainting-Modell, ICCV 2023
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — PP-OCRv3-Erkennungs- und Lesemodelle