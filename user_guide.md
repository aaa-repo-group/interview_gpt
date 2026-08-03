# InterviewGPT User Guide

This guide explains how to start and use InterviewGPT on Windows.

## What This App Does

InterviewGPT opens ChatGPT in a desktop window and shows a caption box that can read text from Windows Live Captions.

You can select useful caption text and send it directly to ChatGPT.

## Requirements

- Windows 10 or Windows 11.
- Microsoft Edge WebView2 Runtime.
- An internet connection.
- A ChatGPT account.
- A valid license key, if the app asks for device authorization.

## First-Time Setup

1. Start the app by opening `InterviewGptBridge.exe`.
2. If the Device Authorization window appears:
   - Click **Copy** beside the device key.
   - Send the device key to the person or team who provides your license.
   - Paste the license key into the **License key** box.
   - Click **Authorize**.
3. Sign in to ChatGPT when the ChatGPT window opens.
4. Turn on Windows Live Captions.

The app saves your ChatGPT session for the current Windows user, so you normally only need to sign in once.

## Turning On Windows Live Captions

1. Press `Windows + Ctrl + L`.
2. Wait for the Live Captions bar to appear.
3. Start the audio, meeting, video, or interview you want captions for.

InterviewGPT reads text from Windows Live Captions and shows it in the caption box.

## Main Windows

### ChatGPT Window

This is the main app window. It contains ChatGPT.

Use it the same way you normally use ChatGPT.

### Caption Bridge Window

This smaller window shows captured caption text.

You can:

- Move it anywhere on your screen.
- Resize it.
- Select text inside it.
- Use the slider at the bottom to change text size.

## Sending Captions To ChatGPT

1. Highlight the caption text you want to send.
2. Press `Enter`.
3. The selected text is inserted and submitted to ChatGPT.

Use `Shift + Enter` if you want to add a new line inside the caption box instead of submitting.

## Using During Interviews

1. Join your interview call or meeting.
2. Turn on Windows Live Captions with `Windows + Ctrl + L`.
3. Keep the Caption Bridge window visible near your meeting window.
4. When the interviewer asks a question, wait for the question to appear in the caption box.
5. Select the question text in the caption box.
6. Press `Enter`.
7. Look at the ChatGPT window to see the suggested answer.

You can select a full question, a short phrase, or several caption lines. The app sends only the selected text.

If the answer is too short or needs more detail, type a follow-up in ChatGPT directly.

## Screen Share Visibility

When **Sensitive Window Protection** is on, the app asks Windows to hide the ChatGPT and Caption Bridge windows from supported screen capture and screen-sharing methods.

This can help prevent the app windows from appearing in some screen shares, recordings, screenshots, or capture tools.

If protection is active, the ChatGPT window shows **Capture protection on**.

## Tray Menu

The app runs from the Windows tray area.

Right-click the tray icon to open the menu:

| Menu Item | What It Does |
| --- | --- |
| **Open ChatGPT** | Shows the main ChatGPT window. |
| **Show captions** | Shows the Caption Bridge window. |
| **Settings...** | Opens app settings. |
| **Hide windows** | Hides the app windows without closing the app. |
| **Caption click-through** | Lets mouse clicks pass through the caption window. |
| **Exit** | Fully closes the app. |







Closing a window usually hides it to the tray. Use **Exit** when you want to fully quit the app.

## Hotkey

The default hotkey for showing or hiding the caption window is:

```text
Ctrl + Alt + Down
```

You can change this in **Settings...** under **Hotkeys**.

## Settings

Open settings from the tray menu by choosing **Settings...**.

### Always Over Main Window

This keeps the Caption Bridge window above the ChatGPT window.

Turn it off if you prefer to manage window order yourself.
