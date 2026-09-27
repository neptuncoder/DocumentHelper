# DocumentHelper

Easy translation service for English to Turkish.

**Select a word in any Windows program, press F10, and see its Turkish meaning.**
Seçtiğiniz kelimenin Türkçe karşılığını F10 ile anında görün.

A small popup opens next to the mouse with the Turkish meanings from
[Cambridge Dictionary](https://dictionary.cambridge.org/dictionary/english-turkish/) and
[Tureng](https://tureng.com). The book icon in the popup shows phrasal verbs with Turkish meanings
and English example sentences from [Oxford Learner's Dictionaries](https://www.oxfordlearnersdictionaries.com),
plus example sentences with Turkish translations from [Tatoeba](https://tatoeba.org).

## Download

Go to [**Releases**](https://github.com/neptuncoder/DocumentHelper/releases/latest) and download
**`DocumentHelper.exe`**, then double-click it.
(The `.zip` contains the same program plus a README.)

- Windows may say **"Windows protected your PC"** because the app is not signed with a paid certificate:
  click **More info → Run anyway**.
- Answer **Yes** to *"Start automatically when you sign in to Windows?"*. The app copies itself to your user
  folder, so you can delete the download afterwards. No administrator rights needed.

Requirements: Windows 10 or 11 (64-bit), an internet connection.

## Use

- Select a word, press **F10**. **Esc** or clicking elsewhere closes the popup. You can also type a word into it.
- The red **TR** icon in the system tray has the menu: start with Windows, settings, **Uninstall…**.
- To use another hotkey, choose *Edit settings* in the tray menu, change `"Hotkey"` (e.g. `"Ctrl+Shift+Y"`)
  and restart the app.

## Privacy

The selected word is sent to the dictionary websites to look it up; nothing else is sent anywhere.
The clipboard is used briefly to read the selection, and its previous content is put back.

## Building

.NET 10 SDK, Windows:

```
dotnet build -c Release DocumentHelper/DocumentHelper.csproj
# Single-file download (what the Releases contain):
dotnet publish DocumentHelper/DocumentHelper.csproj -c Release -r win-x64 -p:ShareableBuild=true -o dist
```
