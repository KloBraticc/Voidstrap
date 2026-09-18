using System;
using System.Runtime.InteropServices;

namespace Voidstrap.UI;

internal static partial class LinuxWebKitInterop
{
	internal const string Gtk = "libgtk-3.so.0";

	internal const string Gdk = "libgdk-3.so.0";

	internal const string Glib = "libglib-2.0.so.0";

	internal const string GObject = "libgobject-2.0.so.0";

	internal const string WebKit = "libwebkit2gtk-4.1.so.0";

	internal const string JavaScriptCore = "libjavascriptcoregtk-4.1.so.0";

	internal const string X11 = "libX11.so.6";

	internal const int GtkWindowPopup = 1;

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	internal delegate bool GSourceFunc(IntPtr data);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	internal delegate void ScriptMessageHandler(IntPtr manager, IntPtr result, IntPtr data);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	internal delegate bool DecidePolicyHandler(IntPtr view, IntPtr decision, int decisionType, IntPtr data);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	internal delegate bool ContextMenuHandler(IntPtr view, IntPtr menu, IntPtr evt, IntPtr hitTest, IntPtr data);

	[LibraryImport(Gtk)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool gtk_init_check(ref int argc, ref IntPtr argv);

	[LibraryImport(Gtk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void gtk_main();

	[LibraryImport(Gtk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr gtk_window_new(int type);

	[LibraryImport(Gtk)]
	internal static partial void gtk_window_set_decorated(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool setting);

	[LibraryImport(Gtk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void gtk_window_resize(IntPtr window, int width, int height);

	[LibraryImport(Gtk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void gtk_window_move(IntPtr window, int x, int y);

	[LibraryImport(Gtk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void gtk_container_add(IntPtr container, IntPtr widget);

	[LibraryImport(Gtk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void gtk_widget_realize(IntPtr widget);

	[LibraryImport(Gtk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void gtk_widget_show_all(IntPtr widget);

	[LibraryImport(Gtk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void gtk_widget_hide(IntPtr widget);

	[LibraryImport(Gtk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void gtk_widget_destroy(IntPtr widget);

	[LibraryImport(Gtk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr gtk_widget_get_window(IntPtr widget);

	[LibraryImport(Gdk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint gdk_x11_window_get_xid(IntPtr window);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	internal delegate void LoadChangedHandler(IntPtr view, int loadEvent, IntPtr data);

	[LibraryImport(Gdk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void gdk_window_invalidate_rect(IntPtr window, IntPtr rect, [MarshalAs(UnmanagedType.I1)] bool invalidateChildren);

	[LibraryImport(Gdk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void gdk_window_process_updates(IntPtr window, [MarshalAs(UnmanagedType.I1)] bool updateChildren);

	[LibraryImport(Glib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint g_idle_add(GSourceFunc function, IntPtr data);

	[LibraryImport(GObject, StringMarshalling = StringMarshalling.Utf8)]
	[UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
	internal static partial ulong g_signal_connect_data(IntPtr instance, string name, IntPtr handler, IntPtr data, IntPtr destroy, int flags);

	[LibraryImport(GObject)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void g_object_unref(IntPtr obj);

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr webkit_web_view_new_with_user_content_manager(IntPtr manager);

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr webkit_user_content_manager_new();

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void webkit_user_content_manager_add_script(IntPtr manager, IntPtr script);

	[LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool webkit_user_content_manager_register_script_message_handler(IntPtr manager, string name);

	[LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial IntPtr webkit_user_script_new(string source, int injectedFrames, int injectionTime, IntPtr allowList, IntPtr blockList);

	[LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void webkit_web_view_load_uri(IntPtr view, string uri);

	[LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void webkit_web_view_evaluate_javascript(IntPtr view, string script, nint length, string? worldName, string? sourceUri, IntPtr cancellable, IntPtr callback, IntPtr userData);

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr webkit_web_view_get_settings(IntPtr view);

	[LibraryImport(WebKit)]
	internal static partial void webkit_settings_set_enable_developer_extras(IntPtr settings, [MarshalAs(UnmanagedType.Bool)] bool enabled);

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void webkit_settings_set_hardware_acceleration_policy(IntPtr settings, int policy);

	[LibraryImport(WebKit)]
	internal static partial void webkit_settings_set_javascript_can_open_windows_automatically(IntPtr settings, [MarshalAs(UnmanagedType.Bool)] bool enabled);

	[LibraryImport(WebKit)]
	internal static partial void webkit_settings_set_enable_html5_database(IntPtr settings, [MarshalAs(UnmanagedType.Bool)] bool enabled);

	[LibraryImport(WebKit)]
	internal static partial void webkit_settings_set_enable_html5_local_storage(IntPtr settings, [MarshalAs(UnmanagedType.Bool)] bool enabled);

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void webkit_web_view_set_background_color(IntPtr view, ref GdkRGBA color);

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr webkit_javascript_result_get_js_value(IntPtr result);

	[LibraryImport(JavaScriptCore)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr jsc_value_to_string(IntPtr value);

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr webkit_navigation_policy_decision_get_navigation_action(IntPtr decision);

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr webkit_navigation_action_get_request(IntPtr action);

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr webkit_uri_request_get_uri(IntPtr request);

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr webkit_response_policy_decision_get_request(IntPtr decision);

	[LibraryImport(WebKit)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool webkit_response_policy_decision_is_mime_type_supported(IntPtr decision);

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void webkit_policy_decision_ignore(IntPtr decision);

	[LibraryImport(WebKit)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void webkit_policy_decision_use(IntPtr decision);

	[LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int setenv(string name, string value, int overwrite);

	[LibraryImport(X11, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial IntPtr XOpenDisplay(string? display);

	[LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int XReparentWindow(IntPtr display, uint child, uint parent, int x, int y);

	[LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int XFlush(IntPtr display);

	[LibraryImport(X11)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int XMapWindow(IntPtr display, uint window);

	[LibraryImport(Glib)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint g_timeout_add(uint interval, GSourceFunc function, IntPtr data);

	[LibraryImport(Gtk)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void gtk_widget_queue_draw(IntPtr widget);

	[StructLayout(LayoutKind.Sequential)]
	internal partial struct GdkRGBA
	{
		public double Red;

		public double Green;

		public double Blue;

		public double Alpha;
	}
}
