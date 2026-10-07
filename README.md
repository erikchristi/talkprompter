# TalkPrompter

A free teleprompter for Windows that listens to your voice and scrolls the script for you.

You read, it follows. If you stop talking or go off script, it waits for you. When you continue, it picks up right where you left off. Everything runs on your computer, so your voice never leaves your machine.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshot-dark.png">
  <source media="(prefers-color-scheme: light)" srcset="docs/screenshot-light.png">
  <img alt="TalkPrompter" src="docs/screenshot-dark.png">
</picture>

## Why I built this

I record a lot of tutorial videos and I was tired of teleprompters that scroll at a fixed speed. I always had to chase the text or wait for it to catch up. So I built one that simply follows my voice.

## Download

Get the latest **TalkPrompter-win-Setup.exe** from the [Releases page](https://github.com/Sven-Bo/talkprompter/releases/latest) and run it. The app keeps itself up to date automatically.

If you prefer no installation, there is also a portable zip on the same page.

You need the .NET 8 Desktop Runtime. Windows will offer to install it if it is missing.

On the first start the app offers to download a voice pack for the language you read in (30 to 70 MB). That is the part that understands your voice. One click and you are done. You can add or switch languages any time under **Settings › Language**.

## Languages

TalkPrompter follows your voice in 12 languages:

English, Czech, Dutch, French, German, Indonesian, Italian, Polish, Portuguese, Russian, Spanish and Turkish.

Tip: in languages other than English, write numbers as words. In Polish, for example, write "dwadzieścia pięć" instead of "25".

Missing your language? [Open an issue](https://github.com/Sven-Bo/talkprompter/issues). A language can be added when there is a good offline speech model for it.

## How to use it

1. Click **Edit script** and paste your text, or drag a **.docx** or **.txt** file onto the window
2. Click **Start**
3. Read

That is really all. Some handy extras:

- Click any word to jump there
- Press **Space** to start or stop. **Ctrl+Alt+Space** works even while another app is focused
- **Camera** turns the app into a narrow strip at the top of your screen, right under your webcam, so nobody sees your eyes move
- **Mirror** flips the text for teleprompter glass
- Messed up a sentence? Just read it again from where you want. The app follows you back

## Features

- Scrolls automatically by listening to your voice
- Pauses when you pause, no fixed scroll speed
- Works 100% offline, no cloud, no account, no subscription
- 12 languages, switchable any time
- Stays out of your recordings: OBS, Zoom, Teams and screenshots don't capture it, but you still see it (Windows 10 2004+, can be switched off in Settings)
- Loads Word documents and reloads them live when you save in Word
- Remembers your recent scripts and where you stopped in each one
- Camera mode, mirror mode, full screen, adjustable text size and column width
- Shows the estimated reading time and how much is left
- Free and open source

## Build it yourself

```
git clone https://github.com/Sven-Bo/talkprompter.git
cd talkprompter
./scripts/Get-VoskModel.ps1
dotnet run --project src/Teleprompter.App
```

The speech models are not part of the repo because they are large. The script above downloads the English one for you, and the app can download any other language itself. Without a model the app runs in a simulation mode so you can still try it.

## License

MIT. Do whatever you want with it.

The voice packs are downloaded from their original sources: the [Vosk models](https://alphacephei.com/vosk/models) by Alpha Cephei (Apache 2.0) and, for Indonesian, a [sherpa-onnx streaming model](https://huggingface.co/spacewave/sherpa-onnx-streaming-zipformer2-id) (MIT).

This version is maintained by Logikfusion. It is based on the original MIT-licensed TalkPrompter, see [LICENSE](LICENSE).
