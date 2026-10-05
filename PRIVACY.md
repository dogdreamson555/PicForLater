# Privacy notice

[Back to README](README.md)

PicForLater processes images locally by default, with no account, ads, or product telemetry. Remote API analysis requires your explicit consent. Checking for updates, downloading components, and receiving images over the local network also involve network access, so the app is not fully offline in every usage scenario.

## Contents

- [Local data and credentials](#local-data-and-credentials)
- [Image analysis and third-party APIs](#image-analysis-and-third-party-apis)
- [Receiving images from phones and device trust](#receiving-images-from-phones-and-device-trust)
- [Screenshot shortcut and clipboard access](#screenshot-shortcut-and-clipboard-access)
- [Update checks and on-demand downloads](#update-checks-and-on-demand-downloads)
- [Controls and data deletion](#controls-and-data-deletion)

## Local data and credentials

Production data is stored in `%LocalAppData%\PicForLater`, including the database and backups, original images, cache, staging, settings, models, and optional components. API keys are stored only in Windows Credential Locker (`PasswordVault`) for the current user. They are not stored in the database, settings, job snapshots, or logs.

The app is a regular unpackaged desktop process without MSIX container isolation. It runs with the current user's permissions and accesses files the user selects or drops, app-managed data directories, the clipboard when explicitly requested by the user, the network, and Windows notifications. Credential Locker protects stored credentials, but cannot defend against malware already running with the same user privileges.

## Image analysis and third-party APIs

### Local analysis

Local analysis uses Windows OCR on the device, or installed local components and models. It does not send images or OCR text to remote analysis providers. User-configured loopback Ollama / vLLM endpoints are handled by services on the same device.

### Data sent for remote analysis

Remote requests are sent to the service configured by the user, with the following scope:

| Analysis mode | Data sent | Data not sent |
| --- | --- | --- |
| OCR text only (`RemoteOcrText`) | Length-limited OCR plain text, language tags, output language policy, reference UTC time / time zone, the selected model, prompt / schema, and output limits | Images, thumbnails, filenames, paths, content hashes, internal IDs, bounding-box coordinates, categories, and other library content |
| Send image (`RemoteVision`) | A temporary PNG generated from the immutable original, reference time / time zone, output language policy, and the fixed request contract | Original image bytes, EXIF / XMP, filenames, paths, hashes, internal IDs, OCR, categories, and library context |

Before sending an image, the app decodes the original, applies its orientation, converts it to sRGB, and re-encodes it. It preserves the original resolution whenever possible, scaling down proportionally only if the image exceeds 16 million pixels or the request limits. Temporary image copies are held only in memory with bounded memory usage, and are released after the call.

### Connection tests and provider boundaries

API connection tests use fixed synthetic text. When the user explicitly runs an image test, it uses the licensed cat image in the repository. Tests do not send user images, user OCR, or library content, but may incur API charges.

Data retention, training, regions, account policies, and charges for third-party APIs depend on the provider and plan you choose. PicForLater cannot promise zero retention or exclusion from training on a provider's behalf. Cancelling a job can prevent requests that have not yet been sent, but cannot recall data already received by the provider or undo charges already incurred. The app disables HTTP redirects and cookies, and does not bypass TLS certificate validation. Public custom endpoints require HTTPS; loopback services are a restricted exception.

## Receiving images from phones and device trust

PicForLater supports LocalSend for receiving images from phones. This feature is disabled by default. When enabled, the app listens on TCP/UDP `53317` on the local network, using local discovery, TLS, and a temporary PIN to receive images selected by the user. Transfers do not pass through a PicForLater or LocalSend cloud relay. Receiving an image alone does not automatically send it to a remote analysis API.

Windows Firewall may request permission when the app first starts listening. After pairing is verified with a temporary PIN, the app stores the SHA-256 fingerprint of the device's TLS certificate locally, allowing later transfers without a PIN. Reinstalling the phone app, clearing its data, or changing its certificate requires pairing again. You can remove trust at any time. Received images become regular library items. If you later select and consent to remote analysis, they are sent according to the [data scope above](#data-sent-for-remote-analysis).

PicForLater is an independent app that is compatible with LocalSend and supports receiving images through LocalSend. It is not an official LocalSend product and has not received official approval or endorsement from LocalSend.

## Screenshot shortcut and clipboard access

The screenshot shortcut is disabled by default and responds only while the feature is enabled and the app is running. After it is triggered, the app detects clipboard changes and reads images during a session lasting up to 60 seconds. It does not read the clipboard while idle or when the feature is disabled, and does not record general keystrokes. Only the enabled state and shortcut are stored in local settings.

To determine when a screenshot session has ended, the app temporarily reads foreground window and process identifiers, including those of other apps you switch to during the session. It does not read window titles, process names, paths, or content. These identifiers and clipboard sequence numbers are used only within the session and are not saved or uploaded. Neither clipboard content that was not imported nor underlying exception details are written to status messages, errors, or logs.

The clipboard cannot prove where an image came from. Images copied from other apps during the session may also be imported, saved to the library, and processed according to the current [analysis configuration](#image-analysis-and-third-party-apis).

<details>
<summary>Screenshot session implementation details</summary>

- The app uses `RegisterHotKey` to register the shortcut without installing a low-level keyboard hook. After it is triggered, the app checks for relevant key releases for up to about one second, then sends `Win+Shift+S` with `SendInput`.
- It copies only PNG or CF_DIBV5 images from the clipboard and does not read other clipboard formats. The clipboard is closed before decoding and import. Linked color profiles are rejected, and the local or network files they point to are not accessed.
- The session compares only foreground HWND / PID and clipboard sequence values. If the screenshot overlay leaves the foreground and no new image is available, the session ends after about 750 milliseconds by default.

</details>

The app does not clear the clipboard or control Windows clipboard history, cross-device synchronization, or the snipping tool's automatic saving. Depending on Windows and snipping tool settings, images may remain in clipboard history, synchronize to other devices, or be saved in the Screenshots folder. Disabling the feature, deleting images in the app, or uninstalling it does not delete these system copies. You can manage them in Windows [clipboard settings](https://support.microsoft.com/en-us/windows/apps/using-the-clipboard) and [snipping tool settings](https://support.microsoft.com/en-us/windows/apps/use-snipping-tool-to-capture-screenshots).

## Update checks and on-demand downloads

### Manual update checks

The app requests the fixed GitHub Releases API only after you click "Check for updates". GitHub receives ordinary network information, such as your IP address, request time, and a `PicForLater/M.m.p` User-Agent containing the current three-part version number. Requests contain no images, OCR text, library content, settings, API keys, or other credentials.

Starting or resuming the app, or opening the settings page, does not check for updates. Only clicking "View release page" opens the Release page constructed from a verified version number. You still download and run the installer manually.

### Downloading components, models, and runtimes

After you confirm, the app downloads the selected components, models, or runtimes from GitHub Releases, Hugging Face, or NVIDIA sources fixed in the manifests. It does not automatically download large models in the background. Online Setup downloads missing prerequisite runtimes as needed; offline Setup includes all prerequisite runtimes. See [ADR 0015](docs/adr/0015-github-unpackaged-setup-and-optional-local-runtime.md) for sources and installation scope.

## Controls and data deletion

You can switch back to local mode, revoke remote consent, delete credentials, or cancel jobs whose requests have not yet been sent. You can also disable receiving images from phones, remove device trust, or disable the screenshot shortcut. Disabling a feature does not delete images already imported.

Normal uninstallation removes the program, shortcuts, notification registration, and uninstall entry, but preserves `%LocalAppData%\PicForLater`. To delete local data, permanently delete the relevant content in the app, or exit the app and delete the entire data directory yourself. This is not a guarantee of secure erasure. Deleting a remote profile or local credentials also does not revoke the key on the provider's side. Revoke it in the provider's console as well if needed.

For implementation-level data boundaries, see the [architecture decision records](docs/adr/). See [SECURITY.md](SECURITY.md) for reporting security issues. Do not submit keys, private images, or details of undisclosed vulnerabilities in public Issues.
