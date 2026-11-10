# Halerith Addon Client

Halerith Addon Client is a desktop application for Minecraft: Java Edition. It helps you create and launch local instances, manage local modpacks, and synchronize content for Minecraft servers you are authorized to use.

The Client runs on Windows, Linux, and macOS. This repository contains the Client source. Halerith operates the separate Addon platform that supplies account-specific managed server lists, manifests, and files.

## What it does

- Create and launch local Minecraft instances and manage local modpacks.
- Sign in with Minecraft accounts and choose the account used for managed server access.
- Display the selected account's Trial, Free, or subscription status as reported by Halerith's services.
- Synchronize the managed servers and content returned for the selected Minecraft account.

Local instances and local modpacks do not require a Halerith subscription. The Addon platform decides which managed servers and files an authenticated Minecraft account may receive. A Client fork or a locally displayed status does not grant access to Halerith's managed services.

## Build from source

Install the .NET 10 SDK. From the repository root, build the desktop project for your operating system:

```sh
# Windows
dotnet build HalerithAddonClient/HalerithAddonClient.csproj -c Debug -f net10.0-windows

# Linux or macOS
dotnet build HalerithAddonClient/HalerithAddonClient.csproj -c Debug -f net10.0
```

These commands produce development builds from the Client source alone. Official signed and packaged releases use private installer, migrator and Linux DEB helper projects outside this source folder. Building the source does not provide Halerith signing credentials or access to managed content beyond what your authenticated Minecraft account is authorized to receive.

CurseForge access for local instances is optional. When a build has no embedded CurseForge API key, enter your own key under **Settings → Application → CurseForge API Key**. Without a key, CurseForge search and update checks are unavailable; existing verified files remain playable, and already resolved pack files can still be installed through their exact CurseForge file page and a hash-verified manual import. A newly imported CurseForge pack cannot resolve its referenced files until a key is available. Modrinth and Halerith-managed server access do not use this key.

## Licence

Halerith-authored Client code is available under the [PolyForm Noncommercial License 1.0.0](LICENSE). It permits noncommercial use, modification, and redistribution under its terms; forks and modified builds remain subject to those terms. Commercial use of the Client code, including selling a fork or using it in a revenue-generating product or platform, requires a separate licence from Halerith. Third-party dependencies and any material carrying its own notice remain under their respective terms.

The Halerith icon files in `HalerithAddonClient.Core/Assets/` are © 2026 Halerith. You may use them with a personal, noncommercial build or fork of this Client. Halerith retains copyright and branding rights; using the icons or Halerith names and logos as branding for another product requires separate permission.

The Client Agreement presented by official Client releases covers the installed application and account-free Trial or Free managed access through it. The separately presented Addon Platform Agreement covers use of the online Platform through a Halerith account, including Manager functions, Client-facing online services and subscriptions. These online functions belong to one Halerith Addon Platform.

## Feedback and contributions

Bug reports and suggestions are welcome. Please [contact Halerith](mailto:info@halerith.com) before sending a code contribution. Before code can be merged, contributors must confirm they have the rights to submit it and agree to its use under this repository's license.
