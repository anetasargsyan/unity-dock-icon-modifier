#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN || UNITY_EDITOR_LINUX
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace ProjectDockIcon
{
    [Serializable]
    internal sealed class DockIconSettings
    {
        private const string Path = "ProjectSettings/EditorDockIcon.json";

        public bool useAppIcon = true;
        public string textureId = "";

        internal static DockIconSettings Load() => File.Exists(Path)
            ? JsonUtility.FromJson<DockIconSettings>(File.ReadAllText(Path)) ?? new DockIconSettings()
            : new DockIconSettings();

        internal void Save() => File.WriteAllText(Path, JsonUtility.ToJson(this, true));

        internal Texture2D Texture =>
            GlobalObjectId.TryParse(textureId ?? "", out var id)
                ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as Texture2D
                : null;
    }

    [InitializeOnLoad]
    internal sealed class EditorDockIcon : EditorWindow
    {
        private static DockIconSettings settings;
        private static double nextCheck;
        private static Texture2D appliedTexture;
        private static Hash128 appliedHash;

        static EditorDockIcon()
        {
            EditorApplication.delayCall += Initialize;
        }

        private static void Initialize()
        {
            // The EditorWindow type can initialize during Unity's deserialization.
            if (Application.isBatchMode)
                return;

            EditorApplication.projectChanged += QueueApply;
            AssemblyReloadEvents.beforeAssemblyReload += ResetIcon;
            Apply();
        }

        [MenuItem("Tools/Dock Icon")]
        private static void Open()
        {
            var window = GetWindow<EditorDockIcon>(true, "Dock Icon");
            window.minSize = window.maxSize = new Vector2(320, 80);
        }

        private void OnGUI()
        {
            var current = GetSettings();
            var previousTexture = current.Texture;
            EditorGUI.BeginChangeCheck();
            Texture2D texture;
            using (new EditorGUI.DisabledScope(current.useAppIcon))
            {
                // Texture2D defaults to a large preview in EditorGUILayout.ObjectField.
                Rect row = EditorGUILayout.GetControlRect();
                texture = (Texture2D)EditorGUI.ObjectField(row, "Texture", previousTexture, typeof(Texture2D), false);
            }

            bool useAppIcon = EditorGUILayout.Toggle("Use App Icon", current.useAppIcon);
            if (EditorGUI.EndChangeCheck())
            {
                if (texture != previousTexture)
                    current.textureId = texture ? GlobalObjectId.GetGlobalObjectIdSlow(texture).ToString() : "";

                current.useAppIcon = useAppIcon;
                try
                {
                    current.Save();
                    QueueApply();
                }
                catch (Exception exception)
                {
                    settings = new DockIconSettings { useAppIcon = false };
                    ResetIcon();
                    Debug.LogWarning($"Dock Icon: {exception.Message}");
                }
            }
        }

        private static DockIconSettings GetSettings()
        {
            try
            {
                return settings ??= DockIconSettings.Load();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Dock Icon: {exception.Message}");
                return settings = new DockIconSettings { useAppIcon = false };
            }
        }

        private static void QueueApply()
        {
            EditorApplication.delayCall -= Apply;
            EditorApplication.delayCall += Apply;
        }

        private static void Apply()
        {
            try
            {
                var current = GetSettings();
                var texture = current.useAppIcon
                    ? LargestIcon(NamedBuildTarget.Standalone) ?? LargestIcon(NamedBuildTarget.Unknown)
                    : current.Texture;

                if (!texture)
                {
                    ResetIcon();
                    return;
                }

                string assetPath = AssetDatabase.GetAssetPath(texture);
                var hash = AssetDatabase.GetAssetDependencyHash(assetPath);
                if (PlatformIcon.Active && texture == appliedTexture && hash == appliedHash)
                    return;

                ResetIcon();
                var copy = ReadIcon(texture);
                try
                {
                    PlatformIcon.Apply(copy);
                    if (PlatformIcon.Active)
                    {
                        appliedTexture = texture;
                        appliedHash = hash;
                        EditorApplication.update += RefreshIcon;
                    }
                }
                finally
                {
                    DestroyImmediate(copy);
                }
            }
            catch (Exception exception)
            {
                ResetIcon();
                Debug.LogWarning($"Dock Icon: {exception.Message}");
            }
        }

        private static Texture2D LargestIcon(NamedBuildTarget target)
        {
            Texture2D largest = null;
            foreach (var candidate in PlayerSettings.GetIcons(target, IconKind.Any) ?? Array.Empty<Texture2D>())
            {
                if (candidate && (!largest || candidate.width * candidate.height > largest.width * largest.height))
                    largest = candidate;
            }
            return largest;
        }

        private static Texture2D ReadIcon(Texture2D texture)
        {
            // Copy through a render target so imported textures can keep Read/Write disabled.
#if UNITY_EDITOR_OSX
            const int maxSize = 512;
#elif UNITY_EDITOR_WIN
            const int maxSize = 256;
#else
            const int maxSize = 128;
#endif
            float scale = Mathf.Min(1f, (float)maxSize / Mathf.Max(texture.width, texture.height));
            int width = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale));
            var previous = RenderTexture.active;
            bool previousSRGBWrite = GL.sRGBWrite;
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D copy = null;
            try
            {
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                return copy;
            }
            catch
            {
                DestroyImmediate(copy);
                throw;
            }
            finally
            {
                GL.sRGBWrite = previousSRGBWrite;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static void RefreshIcon()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < nextCheck || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            nextCheck = now + 1;
            try
            {
                PlatformIcon.Refresh();
            }
            catch (Exception exception)
            {
                ResetIcon();
                Debug.LogWarning($"Dock Icon: {exception.Message}");
            }
        }

        private static void ResetIcon()
        {
            EditorApplication.update -= RefreshIcon;
            appliedTexture = null;
            PlatformIcon.Reset();
        }
    }

#if UNITY_EDITOR_OSX
    internal static class PlatformIcon
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private static IntPtr app, icon, appliedIcon, getIcon, setIcon, release;

        [DllImport(ObjC, EntryPoint = "objc_getClass")]
        private static extern IntPtr GetClass(string name);

        [DllImport(ObjC, EntryPoint = "sel_registerName")]
        private static extern IntPtr Selector(string name);

        // Keep the signatures fixed for the Objective-C calling convention on Apple silicon.
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr target, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr target, IntPtr selector, IntPtr argument);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr target, IntPtr selector, byte[] bytes, UIntPtr length);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoid(IntPtr target, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoid(IntPtr target, IntPtr selector, IntPtr argument);

        private static IntPtr MakeImage(byte[] png)
        {
            if (png == null || png.Length == 0)
                return IntPtr.Zero;

            var alloc = Selector("alloc");
            var data = Send(Send(GetClass("NSData"), alloc), Selector("initWithBytes:length:"), png, (UIntPtr)png.Length);
            if (data == IntPtr.Zero)
                return IntPtr.Zero;

            try
            {
                return Send(Send(GetClass("NSImage"), alloc), Selector("initWithData:"), data);
            }
            finally
            {
                SendVoid(data, release);
            }
        }

        internal static void Apply(Texture2D texture)
        {
            getIcon = Selector("applicationIconImage");
            setIcon = Selector("setApplicationIconImage:");
            release = Selector("release");
            app = Send(GetClass("NSApplication"), Selector("sharedApplication"));
            if (app == IntPtr.Zero)
                return;

            icon = MakeImage(texture.EncodeToPNG());
            if (icon != IntPtr.Zero)
                InstallIcon();
        }

        internal static bool Active => icon != IntPtr.Zero;

        internal static void Refresh()
        {
            if (Send(app, getIcon) != appliedIcon)
                InstallIcon();
        }

        private static void InstallIcon()
        {
            SendVoid(app, setIcon, icon);
            appliedIcon = Send(app, getIcon);
        }

        internal static void Reset()
        {
            if (icon == IntPtr.Zero)
                return;

            SendVoid(app, setIcon, IntPtr.Zero); // AppKit restores this Unity installation's bundled icon.
            SendVoid(icon, release);
            icon = IntPtr.Zero;
        }
    }
#elif UNITY_EDITOR_WIN
    internal static class PlatformIcon
    {
        private const int SetIcon = 0x0080;
        private const int GetIcon = 0x007F;
        private static readonly Guid AppModel = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private static readonly uint[] PropertyIds = { 2, 3, 4, 5 }; // Command, icon, display name, AppUserModelID
        private static IntPtr window, smallIcon, bigIcon, originalSmall, originalBig;
        private static string[] originalProperties, properties;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            internal Guid format;
            internal uint id;
        }

        // Unity's Windows Editor is 64-bit. VT_LPWSTR uses the pointer at offset 8.
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropertyValue
        {
            [FieldOffset(0)] internal ushort type;
            [FieldOffset(8)] internal IntPtr text;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropertyValue value);
            void SetValue(ref PropertyKey key, ref PropertyValue value);
            void Commit();
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropertyValue value);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr instance, string path, uint type, int width, int height, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr parameter, IntPtr data);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        internal static bool Active => properties != null;

        internal static void Apply(Texture2D texture)
        {
            string project = Path.GetDirectoryName(Application.dataPath);
            string folder = Path.Combine(project, "Library", "EditorDockIcon");
            Directory.CreateDirectory(folder);

            byte[] ico = MakeIconFile(texture);
            string path = Path.Combine(folder, Hash128.Compute(ico).ToString() + ".ico");
            if (!File.Exists(path))
                File.WriteAllBytes(path, ico);

            const uint imageIcon = 1;
            const uint loadFromFile = 0x0010;
            smallIcon = LoadImage(IntPtr.Zero, path, imageIcon, GetSystemMetrics(49), GetSystemMetrics(50), loadFromFile);
            bigIcon = LoadImage(IntPtr.Zero, path, imageIcon, GetSystemMetrics(11), GetSystemMetrics(12), loadFromFile);
            if (smallIcon == IntPtr.Zero || bigIcon == IntPtr.Zero)
                throw new InvalidOperationException("Windows could not load the generated icon.");

            properties = new[]
            {
                $"\"{EditorApplication.applicationPath}\" -projectPath \"{project}\"",
                path + ",0",
                Path.GetFileName(project),
                "Unity.Editor.Project." + Hash128.Compute(project.ToUpperInvariant())
            };
            Refresh();
        }

        internal static void Refresh()
        {
            if (window != IntPtr.Zero && IsWindow(window))
            {
                // Unity may replace the window icon during editor activity.
                if (SendMessage(window, GetIcon, IntPtr.Zero, IntPtr.Zero) != smallIcon)
                    SendMessage(window, SetIcon, IntPtr.Zero, smallIcon);
                if (SendMessage(window, GetIcon, (IntPtr)1, IntPtr.Zero) != bigIcon)
                    SendMessage(window, SetIcon, (IntPtr)1, bigIcon);
                return;
            }

            using var process = System.Diagnostics.Process.GetCurrentProcess();
            window = process.MainWindowHandle;
            originalProperties = null;
            if (window == IntPtr.Zero)
                return;

            originalSmall = SendMessage(window, SetIcon, IntPtr.Zero, smallIcon);
            originalBig = SendMessage(window, SetIcon, (IntPtr)1, bigIcon);
            var store = GetStore(window);
            try
            {
                originalProperties = ReadProperties(store);
                WriteProperties(store, properties);
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        internal static void Reset()
        {
            try
            {
                if (window == IntPtr.Zero || !IsWindow(window))
                    return;

                SendMessage(window, SetIcon, IntPtr.Zero, originalSmall);
                SendMessage(window, SetIcon, (IntPtr)1, originalBig);
                if (originalProperties != null)
                {
                    var store = GetStore(window);
                    try { WriteProperties(store, originalProperties); }
                    finally { Marshal.ReleaseComObject(store); }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Dock Icon: {exception.Message}");
            }
            finally
            {
                if (smallIcon != IntPtr.Zero)
                    DestroyIcon(smallIcon);
                if (bigIcon != IntPtr.Zero)
                    DestroyIcon(bigIcon);
                window = smallIcon = bigIcon = IntPtr.Zero;
                properties = originalProperties = null;
            }
        }

        private static IPropertyStore GetStore(IntPtr hwnd)
        {
            var iid = typeof(IPropertyStore).GUID;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
            return store;
        }

        private static string[] ReadProperties(IPropertyStore store)
        {
            var values = new string[PropertyIds.Length];
            for (int i = 0; i < values.Length; i++)
            {
                var key = new PropertyKey { format = AppModel, id = PropertyIds[i] };
                store.GetValue(ref key, out var value);
                try
                {
                    if (value.type == 31) // VT_LPWSTR
                        values[i] = Marshal.PtrToStringUni(value.text);
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            return values;
        }

        private static void WriteProperties(IPropertyStore store, string[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                var key = new PropertyKey { format = AppModel, id = PropertyIds[i] };
                var value = new PropertyValue();
                if (values[i] != null)
                {
                    value.type = 31;
                    value.text = Marshal.StringToCoTaskMemUni(values[i]);
                }
                try { store.SetValue(ref key, ref value); }
                finally { Marshal.FreeCoTaskMem(value.text); }
            }
        }

        private static byte[] MakeIconFile(Texture2D texture)
        {
            // ICO uses a square BGRA bitmap with bottom-up rows and a 1-bit mask.
            int size = Mathf.Max(texture.width, texture.height);
            int maskStride = ((size + 31) / 32) * 4;
            var mask = new byte[maskStride * size];
            var pixels = texture.GetPixels32();
            var bitmap = new byte[size * size * 4];
            int left = (size - texture.width) / 2;
            int bottom = (size - texture.height) / 2;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int sourceX = x - left;
                    int sourceY = y - bottom;
                    var color = sourceX >= 0 && sourceX < texture.width && sourceY >= 0 && sourceY < texture.height
                        ? pixels[sourceY * texture.width + sourceX]
                        : default;
                    int index = (y * size + x) * 4;
                    bitmap[index] = color.b;
                    bitmap[index + 1] = color.g;
                    bitmap[index + 2] = color.r;
                    bitmap[index + 3] = color.a;
                    if (color.a == 0)
                        mask[y * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)0); // Reserved
            writer.Write((ushort)1); // Icon file
            writer.Write((ushort)1); // One image
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((ushort)0); // Color count and reserved byte
            writer.Write((ushort)1); // Planes
            writer.Write((ushort)32); // Bits per pixel
            writer.Write(40 + bitmap.Length + mask.Length);
            writer.Write(22); // Offset of the DIB
            writer.Write(40); // BITMAPINFOHEADER size
            writer.Write(size);
            writer.Write(size * 2); // Bitmap and mask heights combined
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(new byte[24]); // BI_RGB and remaining zero-valued header fields
            writer.Write(bitmap);
            writer.Write(mask);
            return stream.ToArray();
        }
    }
#elif UNITY_EDITOR_LINUX
    internal static class PlatformIcon
    {
        private const string Xcb = "libxcb.so.1";
        private const uint Cardinal = 6, Window = 33;
        private static IntPtr connection;
        private static uint root, clientList, windowPid, windowIcon, pid;
        private static uint[] pixels;
        private static readonly System.Collections.Generic.Dictionary<uint, uint[]> originalIcons =
            new System.Collections.Generic.Dictionary<uint, uint[]>();

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenIterator
        {
            internal IntPtr data;
            internal int remaining;
            internal int index;
        }

        [DllImport(Xcb)] private static extern IntPtr xcb_connect(IntPtr name, out int screen);
        [DllImport(Xcb)] private static extern int xcb_connection_has_error(IntPtr conn);
        [DllImport(Xcb)] private static extern void xcb_disconnect(IntPtr conn);
        [DllImport(Xcb)] private static extern IntPtr xcb_get_setup(IntPtr conn);
        [DllImport(Xcb)] private static extern ScreenIterator xcb_setup_roots_iterator(IntPtr setup);
        [DllImport(Xcb)] private static extern void xcb_screen_next(ref ScreenIterator iterator);
        [DllImport(Xcb)] private static extern uint xcb_intern_atom(IntPtr conn, byte onlyIfExists, ushort length, string name);
        [DllImport(Xcb)] private static extern IntPtr xcb_intern_atom_reply(IntPtr conn, uint cookie, IntPtr error);
        [DllImport(Xcb)] private static extern uint xcb_get_property(IntPtr conn, byte delete, uint window, uint property, uint type, uint offset, uint length);
        [DllImport(Xcb)] private static extern IntPtr xcb_get_property_reply(IntPtr conn, uint cookie, IntPtr error);
        [DllImport(Xcb)] private static extern IntPtr xcb_get_property_value(IntPtr reply);
        [DllImport(Xcb)] private static extern int xcb_get_property_value_length(IntPtr reply);
        [DllImport(Xcb)] private static extern uint xcb_change_property_checked(IntPtr conn, byte mode, uint window, uint property,
            uint type, byte format, uint length, [In] uint[] values);
        [DllImport(Xcb)] private static extern uint xcb_delete_property_checked(IntPtr conn, uint window, uint property);
        [DllImport(Xcb)] private static extern IntPtr xcb_request_check(IntPtr conn, uint cookie);
        [DllImport("libc.so.6")] private static extern void free(IntPtr pointer);

        internal static bool Active => pixels != null;

        internal static void Apply(Texture2D texture)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
                return;

            connection = xcb_connect(IntPtr.Zero, out var screen);
            if (connection == IntPtr.Zero || xcb_connection_has_error(connection) != 0)
            {
                Reset();
                return;
            }

            var screens = xcb_setup_roots_iterator(xcb_get_setup(connection));
            for (int index = 0; index < screen && screens.remaining > 0; index++)
                xcb_screen_next(ref screens);
            if (screens.remaining == 0)
            {
                Reset();
                return;
            }

            root = unchecked((uint)Marshal.ReadInt32(screens.data));
            clientList = Atom("_NET_CLIENT_LIST");
            windowPid = Atom("_NET_WM_PID");
            windowIcon = Atom("_NET_WM_ICON");
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            pid = (uint)process.Id;

            pixels = PackIcon(texture);
            Refresh();
        }

        private static uint[] PackIcon(Texture2D texture)
        {
            var colors = texture.GetPixels32();
            var values = new uint[colors.Length + 2];
            values[0] = (uint)texture.width;
            values[1] = (uint)texture.height;
            // EWMH orders ARGB pixels top to bottom. Unity returns bottom to top.
            for (int y = 0; y < texture.height; y++)
            {
                int sourceRow = (texture.height - 1 - y) * texture.width;
                int destinationRow = 2 + y * texture.width;
                for (int x = 0; x < texture.width; x++)
                {
                    Color32 color = colors[sourceRow + x];
                    values[destinationRow + x] =
                        ((uint)color.a << 24) | ((uint)color.r << 16) | ((uint)color.g << 8) | color.b;
                }
            }

            return values;
        }

        internal static void Refresh()
        {
            var windows = Read(root, clientList, Window, 16384);
            if (windows == null)
                return;

            foreach (uint window in windows)
            {
                if (originalIcons.ContainsKey(window))
                    continue;

                if (!OwnsWindow(window))
                    continue;

                var original = Read(window, windowIcon, Cardinal, 1048576);
                if (Write(window, pixels))
                    originalIcons.Add(window, original);
            }
        }

        internal static void Reset()
        {
            if (connection == IntPtr.Zero)
                return;

            try
            {
                foreach (var entry in originalIcons)
                {
                    if (OwnsWindow(entry.Key))
                        Write(entry.Key, entry.Value);
                }
            }
            finally
            {
                originalIcons.Clear();
                pixels = null;
                xcb_disconnect(connection);
                connection = IntPtr.Zero;
            }
        }

        private static bool OwnsWindow(uint window)
        {
            var owner = Read(window, windowPid, Cardinal, 1);
            return owner != null && owner.Length == 1 && owner[0] == pid;
        }

        private static uint Atom(string name)
        {
            uint cookie = xcb_intern_atom(connection, 0, (ushort)name.Length, name);
            var reply = xcb_intern_atom_reply(connection, cookie, IntPtr.Zero);
            if (reply == IntPtr.Zero)
                throw new InvalidOperationException($"X11 could not resolve {name}.");

            try { return unchecked((uint)Marshal.ReadInt32(reply, 8)); }
            finally { free(reply); }
        }

        private static uint[] Read(uint window, uint property, uint type, uint limit)
        {
            uint cookie = xcb_get_property(connection, 0, window, property, type, 0, limit);
            var reply = xcb_get_property_reply(connection, cookie, IntPtr.Zero);
            if (reply == IntPtr.Zero)
                return null;

            try
            {
                if (Marshal.ReadByte(reply, 1) != 32)
                    return null;

                var value = xcb_get_property_value(reply);
                int count = xcb_get_property_value_length(reply) / 4;
                var result = new uint[count];
                for (int i = 0; i < count; i++)
                    result[i] = unchecked((uint)Marshal.ReadInt32(value, i * 4));
                return result;
            }
            finally
            {
                free(reply);
            }
        }

        private static bool Write(uint window, uint[] value)
        {
            uint cookie = value == null
                ? xcb_delete_property_checked(connection, window, windowIcon)
                : xcb_change_property_checked(connection, 0, window, windowIcon, Cardinal, 32, (uint)value.Length, value);
            IntPtr error = xcb_request_check(connection, cookie);
            free(error);
            return error == IntPtr.Zero;
        }
    }
#endif
}
#endif
