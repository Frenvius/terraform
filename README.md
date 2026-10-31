> **Alpha**: Terraform is in an early alpha stage. Expect bugs, and back up your world before trying it. Please report any problem on [Discord](https://discord.gg/MUx7h3UZcU) or as a [GitHub issue](https://github.com/Frenvius/terraform/issues), with your `BepInEx/LogOutput.log` attached.

## Description

Terraform adds true 3D terrain digging to Valheim. Vanilla terrain is a heightmap and can only be raised or lowered a few meters; Terraform adds a voxel layer on top of it, so the pickaxe can carve tunnels, caves, arches and overhangs anywhere in the world.

**Discord**: https://discord.gg/MUx7h3UZcU

![A home built under a dug-out hillside](https://raw.githubusercontent.com/Frenvius/terraform/master/.github/media/dugout-house.jpg)

![A rock arch carved through a hill](https://raw.githubusercontent.com/Frenvius/terraform/master/.github/media/rock-arch.jpg)

![A workshop dug underground, below the house](https://raw.githubusercontent.com/Frenvius/terraform/master/.github/media/cave-workshop.jpg)

## Features

- **Dig anywhere with the pickaxe**: hitting the ground opens a crater; keep digging to go deeper, or dig into a wall to carve a tunnel.
- **Seamless terrain**: the dug ground uses the vanilla terrain material, paint and grass, so caves blend into the world around them.
- **Hoe on dug ground**: level, raise and smooth also work on carved terrain.
- **Stone from digging**: the pickaxe drops stone for the ground it removes.
- **Water**: digging below a lake or the sea floods the hole, and openings under water pour out as waterfalls.
- **Water bucket**: crafted at the forge (6 Fine Wood, 2 Tin), it lets you place water in your own pools and moats.
- **Creatures follow you**: monsters can walk into dug holes and caves.
- **Saved with the world**: every dig is stored in the world save, with no extra files.
- **Multiplayer**: digs are synced to every player.

## Installation

Place `Terraform.dll` in the `BepInEx/plugins` folder, or install it with a mod manager.

In multiplayer, the mod is **required on the server and on every client**. The server checks the version on connect and enforces its digging settings.

## Configuration

The config file is `BepInEx/config/com.frenvius.Terraform.cfg`.

| Section | Setting | Default | Description |
|--|--|--|--|
| Digging | BrushSize | 2 | Radius of the dig brush in world units |
| Digging | StonePerCubicMeter | 0.4 | Stone dropped per cubic meter of ground removed |
| Water | BucketCapacity | 4 | Water the bucket holds, in cubic meters |
| Debug | DebugMode | false | Extra logging and debug keys |

## Known limitations

- Digging reaches about 32 meters above and below the ground level of each zone.
- Wards and no-build areas block digging, like the vanilla hoe and pickaxe.

Found a bug? Report it in the `#bugs` channel on [Discord](https://discord.gg/MUx7h3UZcU) or open an issue on [GitHub](https://github.com/Frenvius/terraform/issues), with your `BepInEx/LogOutput.log` attached.

## License

Terraform is licensed under the GNU General Public License v3.0. Copyright (C) 2026 Frenvius.
