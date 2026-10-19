# Installation

Download a pre-built binary from [GitHub Releases](https://github.com/hjc4869/LightPlayer/releases/latest).
The available packages are:

| Platform | Architecture | Download |
| --- | --- | --- |
| Linux | x64 (x86_64) | `im.hjc.LightPlayer-x86_64.flatpak` |
| Linux | ARM64 (aarch64) | `im.hjc.LightPlayer-aarch64.flatpak` |
| Windows | x64 | `LightPlayerSetup-x64.exe` |
| Windows | ARM64 | `LightPlayerSetup-arm64.exe` |
| Windows (MSIX) | x64 and ARM64 | `LightPlayerMSIX.zip` |
| macOS | Apple Silicon (ARM64) | `LightPlayer-osx-arm64.zip` |
| Android | x64 and ARM64 | `im.hjc.LightPlayer-Signed.apk` |
| iOS | ARM64 | `LightPlayer-ios-arm64.ipa` (unsigned) |

## Linux Flatpak

With Flatpak installed, add Flathub for runtime dependencies and install the downloaded bundle
from a terminal in the download directory:

```sh
flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
flatpak install --user ./im.hjc.LightPlayer-x86_64.flatpak
```

On ARM64, use `im.hjc.LightPlayer-aarch64.flatpak` instead. Accept the prompts to install
the required runtime. Launch Light Player from the application menu or run:

```sh
flatpak run im.hjc.LightPlayer
```

To update, download the new bundle for your architecture and install it over the existing app:

```sh
flatpak install --user --reinstall ./im.hjc.LightPlayer-x86_64.flatpak
```

## iOS signing and sideloading

The IPA is unsigned. It needs to be signed with an Apple development certificate and a provisioning
profile for your device before it can run. A sideloading tool can create these using your Apple
Account and sign the IPA during installation.

For example, use AltStore Classic:

1. Install AltServer on a Mac or Windows PC and use it to install AltStore Classic on your device.
   Follow the [macOS setup](https://faq.altstore.io/altstore-classic/how-to-install-altstore-macos)
   or [Windows setup](https://faq.altstore.io/altstore-classic/how-to-install-altstore-windows),
   including the required Apple software on Windows. Connect the device by USB, accept
   **Trust This Computer**, and sign in with your Apple Account when prompted.
2. On the device, open **Settings > General > VPN & Device Management**, select your developer
   account, and trust it. On iOS 16 or later, enable **Settings > Privacy & Security > Developer Mode**,
   restart, and confirm the change.
3. Download `LightPlayer-ios-arm64.ipa` to the device's Files app. In AltStore Classic, open
   **My Apps**, tap **+**, and select the IPA. Use the same Apple Account as AltServer when prompted;
   AltStore signs and installs Light Player. Keep AltServer running and the device connected by USB,
   or use the same Wi-Fi network with Wi-Fi sync enabled.

With a free Apple Account, signed apps expire after seven days and only three sideloaded apps can
be active at once, including AltStore Classic. Use **Refresh All** in AltStore before expiration
while AltServer is reachable; background refresh also requires that connection. A paid Apple
Developer Program membership permits longer signing validity.

To update Light Player, import the newer IPA through the same tool and Apple Account, keeping the
same app identity so it updates the existing installation and preserves its data.

## macOS

The app bundle is not notarized by Apple. After downloading the ZIP:

1. Extract it and move the app bundle to **Applications**.
2. Try opening Light Player once, then dismiss the macOS verification warning.
3. Open **System Settings > Privacy & Security**, scroll to **Security**, and click **Open Anyway**
   for Light Player. Authenticate if prompted, then confirm **Open**.

Only approve the app if you trust the download. See
[Apple's instructions for opening apps from outside the App Store](https://support.apple.com/en-us/102445).

## Windows MSIX

The ZIP contains a signed bundle for both x64 and ARM64, its public signing certificate, installation
scripts, and dependencies. Windows must trust the self-signed certificate before the first install.

1. Extract all of `LightPlayerMSIX.zip` and open the contained folder ending in `_Test`.
2. Open Windows PowerShell in that folder and run the packaged installation script:

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Add-AppDevPackage.ps1
   ```

3. Follow the prompts, accepting administrator elevation and certificate installation. The script
   imports the signing certificate into the local machine's **Trusted People** store and installs
   the app and required dependencies. The execution-policy override applies only to this PowerShell
   process.

For subsequent updates, download and extract the newer ZIP, open its `.msixbundle` with Windows
App Installer, and choose **Update**. The certificate remains trusted across releases, so this
normally needs no script or certificate import. You can also rerun the packaged script to update
and install any required dependencies. Keep the existing app installed to preserve its data.
