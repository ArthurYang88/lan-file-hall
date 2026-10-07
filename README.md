# LAN File Hall

A Windows 10 / 11 x64 desktop app for sharing files, folders, and text messages over a local network. Install and open the app on each computer to discover nearby devices and see everything they have published in one shared file hall.

[Download the latest Windows package](https://github.com/ArthurYang88/lan-file-hall/releases/latest)

The current app interface is in Chinese. Control names in this guide are English translations of their labels.

## Getting started

1. Extract `LanFileHall-Windows-x64-v1.3.zip` on each computer and double-click `Install.cmd`. The installer runs without administrator privileges, installs into the current user's directory, and creates desktop and Start menu shortcuts. You can also run `LanFileHall.exe` directly. Close the previous version before updating.
2. Open the `LAN File Hall` desktop shortcut. If Windows Firewall prompts you, allow access for the network profile used by your local network, usually a private network at home or in an office. The installer does not change firewall settings.
3. Click **Publish Files** or **Publish Folder**, or drag multiple files and folders into the window. Folders can be published directly without creating a ZIP archive.
4. Published items from all online computers appear in the file hall, usually within 5–10 seconds. Select another computer's items and click **Download Selected Files**. The sender does not need to approve each download.
5. Click **Open Download Folder** to find your downloads. They are saved in the app's dedicated folder under `%USERPROFILE%\Downloads`. Duplicate names receive a numeric suffix instead of overwriting existing items.

Publishing records the original file or folder's location. Keep the source at that location while sharing it. Publishing does not copy items into your download folder, and downloads are not automatically published. Incomplete downloads use temporary `.partial` files, which are removed when canceled or when the app closes normally. Completed downloads receive their final names.

## Sharing folders

A published folder appears as a single item, with **Folder** shown in the size column. Downloading transfers its files individually and reconstructs the directory structure, including empty folders and file modification times. A duplicate folder name receives a numeric suffix; existing folders are not merged or overwritten.

An incomplete folder download stays in a temporary `.partial` directory. The app gives it its final name only after the entire transfer succeeds, and removes the temporary directory if the download is canceled or fails. Both computers need v1.2 or later to transfer folders.

Folder downloads use a directory listing captured when the download begins. Keep source files available during the transfer. Symbolic links, directory junctions, and other reparse points are skipped to avoid including content outside the published folder. If an internal file cannot be read, the download fails and displays an error. A single folder currently supports up to 50,000 files and subdirectories in total.

## Text messages

Open the **Text Messages** tab, select an online computer in **Send To**, enter your text, and click **Send** or press Ctrl+Enter. Enter inserts a new line. The tab shows an unread count when messages arrive. Both computers must be online and running v1.3 or later.

Sent and received messages appear in the message list. Select a message to view its full text. Click **Copy Message** to copy the entire original message, or select part of the text and press Ctrl+C. Unicode characters, multiple lines, spaces, and line breaks are preserved.

The sender sees **Delivered** after the recipient's app acknowledges receipt. This does not indicate that the recipient has read the message. If delivery is unconfirmed, the app keeps the input text so you can retry.

Messages travel directly over the local network without accounts or pairing. History is kept only for the current app session, with the most recent 500 messages retained. Each message can contain up to 32,000 characters. Closing the app clears its message history. Offline messaging is not supported.

## Selection and previous uploads

Closing the window exits the app and stops sharing. Other computers remove its items after about 16 seconds. On the next launch, previous publications return under the **Previous Uploads** group, which starts collapsed. New publications appear under **Current Uploads**. Expanding a group lets you select and download its items normally.

**Unpublish** removes an item from the hall without deleting its source. Transfers that have already started may still finish.

The file list uses standard Windows selection:

- Click to select one item.
- Ctrl+click to add or remove individual items from the selection.
- Shift+click to select a continuous range.
- Ctrl+A to select every item in expanded groups.

Automatic refresh preserves selection and group expansion. Multiple selected items can be downloaded or unpublished together. With a mixed selection, downloading applies to other computers' items and unpublishing applies to your own items. Batch downloads run sequentially and continue if an individual item fails. **Cancel Download** stops the entire queue.

## Network behavior

Devices on the same local network can browse and download published items without accounts or pairing. Discovery uses UDP broadcasts on port 45831; transfers use a dynamically assigned TCP port. The app uses no cloud service and does not configure router port forwarding. Network traffic uses unencrypted HTTP.

If another computer does not appear, confirm that both apps are open, the computers can reach each other, and the firewall allows inbound access to the app. Guest Wi-Fi, access point isolation, VPNs, or separate subnets may prevent discovery.

## Features and limitations

- A combined list of files and folders from online computers, with search by item or computer name and a filter for your own publications.
- Direct file and folder publishing, drag and drop, batch downloads, progress, and cancellation.
- Text messages between online computers, delivery acknowledgments, full-text viewing, and copying.
- Download resuming and automatic startup are not supported.
- There is no background server. Items that have not yet been downloaded are unavailable while their publisher's app is closed.

## Building and verification

Built with .NET 8 WinForms, with no third-party library dependencies.

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/app
```

Run `LanFileHall.exe --self-test` to execute two-endpoint integration tests in an isolated temporary directory. Results are written to `self-test-results.txt` next to the executable.

Tests cover UDP discovery, catalog retrieval, SHA-256 verification of a 3 MB transfer, empty files, duplicate names, persistence, access restrictions for unpublished items, path traversal, cancellation, and file size changes. Folder tests cover Unicode paths, nested and empty directories, transfers, and temporary-directory cleanup. Message tests cover bidirectional delivery, exact Unicode and multiline text, delivery acknowledgments, maximum length, duplicate requests, invalid content, and offline failure states.

These tests use networking on one computer and do not replace testing between two physical computers.

## Uninstalling

Close the app, delete `%LOCALAPPDATA%\Programs\LanFileHall`, and remove its desktop and Start menu shortcuts. Settings are stored in `%LOCALAPPDATA%\LanFileHall`. Downloaded files are kept separately; retain or remove them as needed.
