# SimpleNetwork

A storage network mod for [Vintage Story](https://www.vintagestory.at/) 1.22.

Link your chests, crates and vessels into one storage network with cables. Search, sort and craft from all of it at a terminal, reach it wirelessly, automate with importers and exporters, and keep storage organised with priorities and filters.

## Features

- **Network Cables** connect storage into one network, with arms that show what each cable is connected to.
- **Covered cables**: right-click a cable with a chiseled block to hide the cable inside it; the network still runs through.
- **Network Terminal**: a searchable, sortable view of everything stored, with taking and storing by click or shift-click.
- **Built-in crafting** with auto-refill from the network, shift-click craft-all, and recipe lookup that fills the grid for you.
- **Wireless Terminal**: open a linked terminal's network from up to 64 blocks away (configurable).
- **Network Importer / Exporter**: move items between the network and the block they face, using the game's own hopper slot rules.
- **Network Configurator**: give containers a priority and an item filter.
- **Multiplayer-safe**: respects land claims and padlocks, and all actions are checked on the server.

The in-game handbook (press H) has pages for every block and item, plus a "Storage networks" guide.

## Installing

1. Download the latest release zip.
2. Put the zip (don't unpack it) in your Vintage Story `Mods` folder.
3. On a multiplayer server, install it on the server too.

## Configuration

The server's `ModConfig/simplenetwork.json` is created on first start:

| Setting | Default | What it does |
|---|---|---|
| `StorageBlockEntityClasses` | `GenericTypedContainer`, `LabeledChest`, `Crate` | Block entity classes that count as storage (the defaults cover chests, trunks, reed baskets, storage vessels, labeled chests and crates) |
| `StorageBlockCodes` | none | Extra storage by block code, wildcards allowed (e.g. `somemod:bigchest-*`) |
| `ExcludedBlockCodes` | none | Block codes that never count as storage |
| `MaxNetworkBlocks` | 4096 | Largest network (cables and terminals) that will be scanned |
| `WirelessRange` | 64 | Wireless terminal range in blocks; `0` = unlimited |
| `PortIntervalMs` | 1000 | How often importers and exporters move items |
| `PortTransferAmount` | 8 | Items moved each time |
| `EnableWirelessTerminal` | true | Allow the Wireless Terminal |
| `EnableImportExport` | true | Allow the Network Importer and Exporter |
| `EnableConfigurator` | true | Allow the Network Configurator (container priorities and filters) |

Switching a feature off removes its crafting recipe (after a server restart) and stops existing ones from working (also right after `/stn reload`). The blocks and items stay in the game, so worlds that already have them don't break; with the configurator off, saved priorities and filters are ignored.

Commands (`/stn` is short for `/simplenetwork`):

- `/stn check`: look at a block to see whether it counts as network storage
- `/stn reload`: reload the config without restarting (needs the controlserver privilege)

## Building from source

Requirements:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Vintage Story 1.22 installed
- An environment variable `VINTAGE_STORY` pointing at your Vintage Story install folder (on Windows usually `%APPDATA%\Vintagestory`)

Then:

- **Build:** `dotnet build SimpleNetwork/SimpleNetwork.csproj`
- **Run and debug:** open the folder in VS Code (with the C# extension) and press F5. The game starts with the mod loaded.
- **Release zip:** run `./build.ps1` (Windows) or `./build.sh` (Linux/macOS). This checks all JSON files, builds the mod and writes `Releases/simplenetwork_<version>.zip`.

## Project layout

| Folder | Contents |
|---|---|
| `SimpleNetwork/Blocks` | Cable, terminal, importer/exporter blocks and their block entities |
| `SimpleNetwork/Items` | Wireless terminal and network configurator |
| `SimpleNetwork/Network` | Network scanning and caching, permissions, storage settings, network packets |
| `SimpleNetwork/Terminal` | Terminal logic on the server and client, crafting grid handling |
| `SimpleNetwork/Gui` | Terminal and storage settings windows |
| `SimpleNetwork/assets` | Block and item definitions, shapes, textures, recipes and translations |
| `CakeBuild` | The release packaging script used by `build.ps1` / `build.sh` |

## Translations

All text is in `SimpleNetwork/assets/simplenetwork/lang/en.json`. To add a language, copy it to the language's code (for example `de.json`) and translate the values on the right.

## Reporting bugs

Please open an issue on this repository's Issues tab. Include your Vintage Story version, the SimpleNetwork version, what you did, and any error from your client or server log.

## Credits

Made by Vertex101. Developed with the help of Claude, an AI assistant by Anthropic.

## License

[MIT](LICENSE): you may use, change and share this code, as long as the copyright notice is kept.
