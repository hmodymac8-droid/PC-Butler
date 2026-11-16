# PC Butler for Android: Privacy Policy

Last updated: 16 November 2026 (clipboard, apps, benchmark; no host relay)

PC Butler (package `com.hmodymac8droid.pcbutler`) is made by hmodymac8-droid. This page says what the app does with your data. In short: **the app has no account, no server, no ads and no analytics. Your files stay on your devices.**

## What stays on your phone

- **Sorter, Duplicates, Storage.** These tools read file names, sizes and dates on the phone's storage so they can sort Download, find copies and show what takes space. Everything is done on the phone. Nothing about your files is sent anywhere.
- **Cleaner, Search, Apps, Benchmark.** The Cleaner and Search read the same folders you gave the app; Apps lists the apps installed on the phone so you can open, inspect or uninstall them (Android always asks before an uninstall); Benchmark writes a temporary test file in the app's own storage and deletes it. All of it stays on the phone.
- **Recycle Bin.** Files you delete in Duplicates or Storage are moved to a folder that belongs to the app, so you can put them back. They stay on the phone until you empty the bin.
- **Vault.** Files you add are encrypted on the phone (AES-GCM, with a key made from your password). The password is never stored or sent. Without it, nobody, including the developer, can open the vault.
- **Settings and history** (paired devices, the list of past transfers) are saved only inside the app's private storage.

## Courier: files between your devices

Courier sends files directly between your phone and the computers you pair with it, across the internet.

- **Files go device to device.** They are encrypted in transit (TLS) and never pass through a server run by the developer, because there is none. They go straight from one of your devices to the other.
- **Same network.** When the phone and the PC are on the same Wi-Fi, they find each other with a short announcement on the local network, which never leaves it.
- **Pairing.** Each device makes its own certificate. You confirm a pairing code shown on both devices; only paired devices can send files.
- **Finding each other.** To let your devices find each other across different networks, Courier publishes a short record in the public BitTorrent DHT, a worldwide directory run by its users. The record holds a key derived from your Courier ID and your device's public IP address and port. Anyone can read records in that directory, as with any internet connection, which also reveals your IP address to the device you connect to. The DHT contains no file names, file contents or personal details.
- **Your router.** Courier may ask your own router (UPnP or NAT-PMP) to open a port for it while it runs.
- **Received files** are saved to the phone's Download folder, in “PC Butler Courier”.

## Clipboard

- **Sending what you copied.** On the Clipboard page, or with the floating button, text you copied is sent to the PC you are connected to over Courier, and text the PC sends is put on the phone's clipboard. The texts are kept in the app's private storage (the newest 200, plus any you pin) and go nowhere else.
- **Send what I copy by itself** (only in the app downloaded from PC Butler, not in the Google Play version): if you switch on its Accessibility service in Android's settings, the app is told when you copy something in any app and sends that text to the connected PC. It reads nothing else on the screen, and it does nothing until you switch it on.

## Permissions

- **Folders you pick:** the Sorter, Duplicates and Storage work only in the folders you choose with Android's folder picker, and the Vault only takes the files you pick.
- **Storage (Android 10 and older only):** so Courier and the Vault can save files to the Download folder.
- **Notifications and foreground service:** to keep Courier connected and show transfer progress while the app is in the background.
- **Internet and network state, Wi-Fi state and multicast:** for Courier only, to reach your devices and to find them on the same Wi-Fi.
- **Display over other apps:** only for the floating clipboard button, when you switch it on.
- **Request uninstalling apps:** so Apps can ask Android to uninstall an app you chose.

## Sharing and selling

The developer receives no data from the app, so none is shared or sold.

## Children

The app is not aimed at children.

## Deleting your data

Uninstalling the app removes its settings, history, Recycle Bin and Vault from the phone. Take files out of the Vault before uninstalling. Files Courier saved to Download stay until you delete them.

## Changes and contact

If this policy changes, the new version will be published at this address with a new date. For questions, open an issue at https://github.com/hmodymac8-droid/PC-Butler/issues
