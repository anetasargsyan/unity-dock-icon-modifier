# Unity Project Dock Icon

Give each open Unity project its own icon so you can tell them apart at a glance.

Drop `EditorDockIcon.cs` into `Assets/Editor/` in your Unity 6 project, then open **Tools → Dock Icon**. Leave **Use App Icon** checked to use your Player icon, or uncheck it to pick a texture. If the chosen icon is missing, Unity uses its usual icon.

Set it once per project. The choice lives in `ProjectSettings/EditorDockIcon.json` and applies every time you open Unity.

Works with the macOS Dock and Windows taskbar. On Linux, it sets the window icon on X11 and XWayland. Whether it appears in the taskbar depends on your desktop.

If something goes wrong, look for `Dock Icon:` in the Unity Console. To remove it, delete the script and its settings file, then reopen Unity.
