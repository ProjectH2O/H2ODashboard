# H2ODashboard

A virtual dashboard for H2Overdrive, designed to work with [OpenParrot-H2O](https://github.com/ProjectH2O/OpenParrot).

This is a component of [Project H2O](https://github.com/ProjectH2O/ProjectH2O), not intended for standalone use. Please see the Project H2O page for details.

This page contains additional information that may be helpful for developers.

### Features

- Emulate the physical gauges on the H2Overdrive cabinet
- Use custom skins
- Display detailed info such as true speed and boost values
- User-defined split timers
- Compatible with OBS

### Usage

Install [Project H2O](https://github.com/ProjectH2O/ProjectH2O).

With H2Overdrive running on Project H2O, simply open `H2ODashboard.exe` and it should connect to the game automatically. Further instructions are in the window.

To set a custom WR time, edit `H2ODashboard.wr.ini` in a text editor.

### Screenshots

With the `splits` panel and `speedrun` skin:

![Setup 1](https://raw.githubusercontent.com/ProjectH2O/ProjectH2O/main/media/screenshots/london1.jpg)

With the `timeattack` panel and `cabinet` skin:

![Setup 2](https://raw.githubusercontent.com/ProjectH2O/ProjectH2O/main/media/screenshots/quake1.jpg)

### Creating a skin

To get started with making your own skin, see the [skins.md](docs/skins.md) page.

### Development

For more info on the shared memory written by OpenParrot-H2O and read by H2ODashboard, see the [shared-memory.md](docs/shared-memory.md) page.
