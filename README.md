# MonitorSwitch

Kleines Windows-Tray-Tool, das den Eingang eines Monitors per **DDC/CI** (VCP-Code `0x60`) umschaltet – mit einem Klick, einem Hotkey oder einer Desktop-Verknüpfung.

Gedacht für **einen Monitor an mehreren PCs**: Das Tool läuft auf jedem PC, und dort stellt man ein, wohin ein Klick wechselt – normalerweise auf den Eingang des jeweils anderen PCs.

## Bedienung

| Aktion | Wirkung |
|---|---|
| Linksklick aufs Tray-Icon | zum eingestellten Ziel wechseln |
| `Strg+Alt+F12` | zum eingestellten Ziel wechseln (Hotkey änderbar) |
| Desktop-Verknüpfung „Monitor wechseln“ | zum eingestellten Ziel wechseln |
| Rechtsklick aufs Tray-Icon | Menü (siehe unten) |

Das Rechtsklick-Menü bietet:

- **Ziel für Klick und Hotkey** – Eingang, auf den gewechselt wird. Die Liste kommt direkt vom Monitor.
- **Jetzt wechseln zu** – einmalig auf einen beliebigen Eingang springen
- **Eingang umbenennen** – z. B. `HDMI 1` → `Gaming-PC`
- **Pixelreinigung starten** – nur bei ASUS-ROG-OLED-Monitoren, siehe unten
- **Monitor** – welcher Monitor gesteuert wird, falls mehrere DDC/CI unterstützen
- **Mit Windows starten**, **Desktop-Verknüpfung erstellen**, **Konfiguration bearbeiten / neu laden**

## Pixelreinigung (ASUS ROG OLED)

OLED-Monitore von ASUS haben eine Pixelreinigung gegen Einbrennen und Geisterbilder. Normalerweise läuft sie automatisch im Standby oder wird über das Monitor-Menü gestartet. MonitorSwitch kann sie auch per DDC/CI anstoßen:

- Tray-Menü → **Pixelreinigung starten…** (mit Sicherheitsabfrage) oder `MonitorSwitch.exe --clean`
- Der Bildschirm ist dann etwa **6 Minuten** schwarz. In der Zeit den Monitor nicht ausschalten und nicht umschalten, sonst bricht die Reinigung ab. MonitorSwitch hält den PC so lange wach und warnt, wenn man währenddessen wechseln will.
- Technisch: Im Herstellercode `0xFD` wird Bit `0x10` gesetzt, die anderen Bits bleiben unverändert. Der Monitor setzt das Bit danach selbst zurück.
- Getestet mit dem **ROG Swift OLED PG34WCDN**. Für den PG49WCD hat das Projekt [asus-pixel-cleaner](https://codeberg.org/beeb/asus-pixel-cleaner) denselben Mechanismus gefunden. Der Menüpunkt erscheint nur bei Monitoren mit „ROG“ im Namen, deren `0xFD` das Reinigungsbit anbietet.

## Einrichtung mit zwei PCs

1. `MonitorSwitch.exe` bauen (siehe unten) und auf **beiden** PCs in einen beliebigen Ordner legen.
2. Auf PC 1 starten, per Rechtsklick als Ziel den Eingang von **PC 2** wählen.
3. Auf PC 2 starten, als Ziel den Eingang von **PC 1** wählen.
4. Auf beiden PCs „Mit Windows starten“ und bei Bedarf „Desktop-Verknüpfung erstellen“ aktivieren.

Beim ersten Start wird der Monitor automatisch erkannt, und als Ziel wird der erste Eingang gesetzt, der gerade nicht aktiv ist.

> Voraussetzung: **DDC/CI** muss im Monitor-Menü (OSD) aktiviert sein. Bei vielen Monitoren reagiert DDC/CI auch dann, wenn gerade ein anderer Eingang angezeigt wird – so kann jeder PC den Monitor zurückholen.

## Bauen

Es wird kein SDK benötigt: `build.cmd` nutzt den C#-Compiler aus dem .NET Framework 4.x, das bei Windows dabei ist.

```bat
build.cmd
```

Danach liegt `MonitorSwitch.exe` im selben Ordner.

## Kommandozeile

Praktisch für Stream Deck, Aufgabenplanung usw.

```bat
MonitorSwitch.exe                 :: Tray-Modus
MonitorSwitch.exe --target        :: zum eingestellten Ziel wechseln
MonitorSwitch.exe --set 0x11      :: zu einem bestimmten Eingang wechseln
MonitorSwitch.exe --list          :: Monitore und Eingänge anzeigen
MonitorSwitch.exe --clean         :: Pixelreinigung starten (ASUS ROG OLED, läuft ca. 7 min unsichtbar weiter)
```

Übliche Eingangswerte: `0x0F` DisplayPort 1, `0x10` DisplayPort 2, `0x11` HDMI 1, `0x12` HDMI 2, `0x1A` USB-C (herstellerspezifisch).

## Konfiguration

`MonitorSwitch.ini` liegt neben der `.exe`. Ist der Ordner nicht beschreibbar, wird `%APPDATA%\MonitorSwitch` verwendet. Normalerweise pflegt das Tray-Menü die Datei. Jeder PC hat seine eigene Konfiguration, deshalb die `.ini` beim Kopieren nicht mitnehmen.

```ini
[MonitorSwitch]
Monitor=ROG SWIFT PG34WCDN
Target=0x11
Hotkey=Ctrl+Alt+F12
Name_0x11=Gaming-PC
```

`Hotkey=` leer lassen, wenn kein Hotkey gewünscht ist.
