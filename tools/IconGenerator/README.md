# IconGenerator

Erzeugt die ausgelieferten Branding-Assets in `assets/` aus dem Quellbild
`Touchmappingagent.png` im Projektroot.

```bash
dotnet run --project tools/IconGenerator -- Touchmappingagent.png assets
```

## Was erzeugt wird

| Datei | Verwendung |
|---|---|
| `touchmappingagent.ico` | EXE-Icons, Titelleisten, Tray, Installer, Verknüpfungen |
| `logo.png` | Quelle für die Installer-Bitmaps (256×256) |
| `installer-header.bmp` | MUI2-Kopfzeile, 150×57, 24-Bit |
| `installer-welcome.bmp` | MUI2-Willkommen-/Abschlussseite, 164×314, 24-Bit |

## Warum das Icon nicht das ganze Bild ist

Das Quellbild ist ein quadratisches Banner mit drei Monitoren, Wortmarke und Feature-Leiste.
Auf 16 px skaliert ist davon nichts mehr erkennbar. Das Icon ist deshalb der **zentrale
Bildschirm mit Touch-Ring und Cursor**, ausgeschnitten über die Konstanten `MarkX/Y/W/H` in
`Program.cs`. Dieser Ausschnitt ist nahezu quadratisch, füllt die Icon-Fläche ohne Letterboxing
und bleibt bis 16 px auf hellen wie dunklen Taskleisten lesbar.

Wird das Quellbild ausgetauscht, müssen diese vier Konstanten neu bestimmt werden — am besten,
indem man Kandidaten bei echten Icon-Größen nebeneinander rendert, statt sie zu schätzen.

## Formatentscheidungen

* Frames 16/24/32/48 als unkomprimiertes DIB — das Format, das jeder Konsument inklusive NSIS
  ohne Rückfragen verarbeitet, und bei diesen Größen nur wenige KB groß.
* Frames ab 64 px als PNG. Ein 128-px-DIB allein kostet 67 KB; das Icon wird viermal eingebettet
  (beide EXEn, Installer, Uninstaller).
* Installer-Bitmaps als 24-Bit-BMP ohne Alpha — MUI2 lehnt 32-Bit-BMPs mit Alphakanal ab. Das
  transparente Quellbild wird dafür auf Weiß geflacht.
