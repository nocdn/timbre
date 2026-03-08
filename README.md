# timbre

This is an entirely vibe-coded recreation of "wispr flow" or "hex" natively for windows - to test out the limits of gpt-5.4. Safe to say it did a pretty good job. I mainly built this for myself, but if you find it useful, feel free to fork, create issues, or propose changes and I'll get to them.

Quite a large change however, is that since my PC is really not that powerful, there are no local models being downloaded and ran, it uses either Groq's or Fireworks' Whisper-Large-V3(-Turbo) model to transcribe. Honestly, it works much quicker than a local model ever could on here (within 0.5-0.8s for 99% of runs)

### Some features:

- global hotkey to start/stop dictation
- paste transcript into the active app
- transcript history
- microphone selection
- Groq and Fireworks provider support
- background work, and minimising to tray
- configurable model/language
- Windows installer/MSI

### Requirements

- Windows 10/11
- A Groq or Fireworks API key

### Installation and Usage

To install it, head over to the Releases page, and download the latest .msi installer, go through the steps and then run the app you should now have on your system called Timbre

There is also a portable installation version also on the same releases page where you download a zip, extract it and then the portable exe should be in there.

### License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
