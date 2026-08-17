using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DiscordRPC;
using DiscordRPC.Logging;
using DiscordRPC.Message;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public class RPCCustomizerViewModel : INotifyPropertyChanged, IDisposable
{
	private record RpcConfig(string ApplicationId, string AppName, string Details, string State, string LargeImageKey, string SmallImageKey, bool Button1Enabled, bool Button2Enabled, string Button1Label, string Button1Url, string Button2Label, string Button2Url, bool AutoStartRpc);

	private static RPCCustomizerViewModel? _shared;

	public static RPCCustomizerViewModel Shared => _shared ??= new RPCCustomizerViewModel();

	public static RPCCustomizerViewModel? SharedOrNull => _shared;

	private readonly SemaphoreSlim _opGate = new SemaphoreSlim(1, 1);

	private readonly object _rpcLock = new object();

	private DiscordRpcClient _client;

	private bool _isStarting;

	private bool _isStopping;

	private bool _rpcConnected;

	private bool _isLoadingConfig;

	private readonly string _configPath = Path.Combine(Paths.UserData, "discord-rpc.json");

	private CancellationTokenSource _saveCts;

	private CancellationTokenSource _presenceCts;

	private readonly DispatcherTimer _reconnectTimer;

	private readonly Dispatcher _dispatcher;

	private string _applicationId;

	private string _appName;

	private string _details;

	private string _state;

	private string _largeImageKey;

	private string _smallImageKey;

	private bool _button1Enabled;

	private bool _button2Enabled;

	private string _button1Label;

	private string _button1Url;

	private string _button2Label;

	private string _button2Url;

	private string _statusMessage;

	private Brush _statusColor;

	private bool _autoStartRpc;

	private bool _disposed;

	public RelayCommand StartRpcCommand { get; }

	public RelayCommand StopRpcCommand { get; }

	public RelayCommand CloseCommand { get; }

	public RelayCommand UpdatePresenceCommand { get; }

	public bool AutoStartRpc
	{
		get
		{
			return _autoStartRpc;
		}
		set
		{
			SetValue(ref _autoStartRpc, value, "AutoStartRpc");
		}
	}

	public string ApplicationId
	{
		get
		{
			return _applicationId;
		}
		set
		{
			if (SetField(ref _applicationId, value, "ApplicationId"))
			{
				DebouncedSave();
				UpdateCommands();
			}
		}
	}

	public string AppName
	{
		get
		{
			return _appName;
		}
		set
		{
			SetValue(ref _appName, value, "AppName");
		}
	}

	public string Details
	{
		get
		{
			return _details;
		}
		set
		{
			SetValue(ref _details, value, "Details");
		}
	}

	public string State
	{
		get
		{
			return _state;
		}
		set
		{
			SetValue(ref _state, value, "State");
		}
	}

	public string LargeImageKey
	{
		get
		{
			return _largeImageKey;
		}
		set
		{
			SetValue(ref _largeImageKey, value, "LargeImageKey");
		}
	}

	public string SmallImageKey
	{
		get
		{
			return _smallImageKey;
		}
		set
		{
			SetValue(ref _smallImageKey, value, "SmallImageKey");
		}
	}

	public bool Button1Enabled
	{
		get
		{
			return _button1Enabled;
		}
		set
		{
			SetValue(ref _button1Enabled, value, "Button1Enabled");
		}
	}

	public bool Button2Enabled
	{
		get
		{
			return _button2Enabled;
		}
		set
		{
			SetValue(ref _button2Enabled, value, "Button2Enabled");
		}
	}

	public string Button1Label
	{
		get
		{
			return _button1Label;
		}
		set
		{
			SetValue(ref _button1Label, value, "Button1Label");
		}
	}

	public string Button1Url
	{
		get
		{
			return _button1Url;
		}
		set
		{
			SetValue(ref _button1Url, value, "Button1Url");
		}
	}

	public string Button2Label
	{
		get
		{
			return _button2Label;
		}
		set
		{
			SetValue(ref _button2Label, value, "Button2Label");
		}
	}

	public string Button2Url
	{
		get
		{
			return _button2Url;
		}
		set
		{
			SetValue(ref _button2Url, value, "Button2Url");
		}
	}

	public string StatusMessage
	{
		get
		{
			return _statusMessage;
		}
		set
		{
			SetField(ref _statusMessage, value, "StatusMessage");
		}
	}

	public Brush StatusColor
	{
		get
		{
			return _statusColor;
		}
		set
		{
			SetField(ref _statusColor, value, "StatusColor");
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;

	public RPCCustomizerViewModel()
	{
		//IL_0150: Unknown result type (might be due to invalid IL or missing references)
		//IL_0155: Unknown result type (might be due to invalid IL or missing references)
		//IL_0163: Unknown result type (might be due to invalid IL or missing references)
		//IL_016f: Expected O, but got Unknown
		Application current = Application.Current;
		_dispatcher = ((current != null) ? ((DispatcherObject)current).Dispatcher : null) ?? Dispatcher.CurrentDispatcher;
		_appName = "Voidstrap";
		_details = "";
		_state = "";
		_largeImageKey = "large";
		_smallImageKey = "small";
		_button1Label = "Website";
		_button1Url = "https://example.com";
		_button2Label = "Join";
		_button2Url = "https://discord.gg/";
		_statusMessage = "Idle";
		_statusColor = Brushes.Gray;
		StartRpcCommand = new RelayCommand(delegate
		{
			SafeStartRpcAsync();
		}, CanStartRpc);
		StopRpcCommand = new RelayCommand(delegate
		{
			SafeStopRpcAsync();
		}, CanStopRpc);
		UpdatePresenceCommand = new RelayCommand((Action)delegate
		{
			SafeManualUpdateAsync();
		}, (Func<bool>?)null);
		CloseCommand = new RelayCommand((Action)async delegate
		{
			try
			{
				await SafeStopRpcAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
			finally
			{
				(Application.Current?.Windows.OfType<Window>().FirstOrDefault((Window w) => w.IsActive))?.Close();
			}
		}, (Func<bool>?)null);
		_reconnectTimer = new DispatcherTimer((DispatcherPriority)4, _dispatcher)
		{
			Interval = TimeSpan.FromSeconds(10L),
			IsEnabled = false
		};
		_reconnectTimer.Tick += OnReconnectTimerTick;
		LoadConfigInternal();
		AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
	}

	private async void OnReconnectTimerTick(object? sender, EventArgs e)
	{
		await CheckReconnectAsync().ConfigureAwait(continueOnCapturedContext: false);
	}

	private void OnProcessExit(object? sender, EventArgs e)
	{
		try
		{
			SaveConfigInternal();
		}
		catch
		{
		}
	}

	public void StartRpc()
	{
		_ = SafeStartRpcAsync();
	}

	public void StopRpc()
	{
		_ = SafeStopRpcAsync();
	}

	private bool SetValue<T>(ref T field, T value, [CallerMemberName] string name = null)
	{
		if (!SetField(ref field, value, name))
		{
			return false;
		}
		DebouncedSave();
		SchedulePresenceUpdate();
		return true;
	}

	private void DebouncedSave()
	{
		if (!_isLoadingConfig && !_disposed)
		{
			ReplaceCancellation(ref _saveCts);
			DebounceAsync(async delegate(CancellationToken ct)
			{
				SafeUpdateStatus("Saving pending...", Brushes.DarkGray);
				await Task.Delay(1000, ct).ConfigureAwait(continueOnCapturedContext: false);
				SaveConfigInternal();
			}, _saveCts.Token);
		}
	}

	private void SchedulePresenceUpdate()
	{
		if (!_isLoadingConfig && !_disposed && _client != null && _rpcConnected)
		{
			ReplaceCancellation(ref _presenceCts);
			DebounceAsync(async delegate(CancellationToken ct)
			{
				await Task.Delay(1500, ct).ConfigureAwait(continueOnCapturedContext: false);
				await _dispatcher.InvokeAsync((Action)UpdatePresence, (DispatcherPriority)4);
			}, _presenceCts.Token);
		}
	}

	private static async Task DebounceAsync(Func<CancellationToken, Task> work, CancellationToken ct)
	{
		try
		{
			await work(ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private void DispatcherInvokeSafe(Action action)
	{
		try
		{
			if (_dispatcher != null && !_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
			{
				if (_dispatcher.CheckAccess())
				{
					action();
				}
				else
				{
					_dispatcher.BeginInvoke((Delegate)action, (DispatcherPriority)4, Array.Empty<object>());
				}
			}
		}
		catch
		{
		}
	}

	private void SafeUpdateStatus(string msg, Brush color)
	{
		DispatcherInvokeSafe(delegate
		{
			StatusMessage = msg;
			StatusColor = color;
		});
	}

	private void UpdateCommands()
	{
		DispatcherInvokeSafe(delegate
		{
			StartRpcCommand.RaiseCanExecuteChanged();
			StopRpcCommand.RaiseCanExecuteChanged();
		});
	}

	private bool CanStartRpc()
	{
		if (!_isStarting && !_isStopping)
		{
			return !string.IsNullOrWhiteSpace(ApplicationId);
		}
		return false;
	}

	private bool CanStopRpc()
	{
		if (!_isStopping)
		{
			return _client != null;
		}
		return false;
	}

	private async Task SafeStartRpcAsync()
	{
		if (_disposed || !CanStartRpc())
		{
			return;
		}
		await _opGate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			_isStarting = true;
			UpdateCommands();
			SafeUpdateStatus("Starting Discord RPC...", Brushes.Orange);
			StopClientIfRunning();
			if (string.IsNullOrWhiteSpace(ApplicationId))
			{
				SafeUpdateStatus("Missing Application ID", Brushes.Red);
				return;
			}
			_rpcConnected = false;
			DiscordRpcClient discordRpcClient = new DiscordRpcClient(ApplicationId)
			{
				Logger = new ConsoleLogger
				{
					Level = LogLevel.Warning
				}
			};
			discordRpcClient.OnReady += OnClientReady;
			discordRpcClient.OnClose += OnClientClose;
			discordRpcClient.OnError += OnClientError;
			bool flag;
			try
			{
				flag = discordRpcClient.Initialize();
			}
			catch (Exception ex)
			{
				SafeUpdateStatus("Failed to initialize RPC client: " + ex.Message, Brushes.Red);
				ReleaseClient(discordRpcClient);
				return;
			}
			if (!flag)
			{
				SafeUpdateStatus("Failed to initialize RPC client", Brushes.Red);
				ReleaseClient(discordRpcClient);
				return;
			}
			lock (_rpcLock)
			{
				_client = discordRpcClient;
			}
			_reconnectTimer.IsEnabled = true;
			SafeUpdateStatus("Discord RPC running", Brushes.DeepSkyBlue);
		}
		finally
		{
			_isStarting = false;
			UpdateCommands();
			_opGate.Release();
		}
	}

	private async Task SafeStopRpcAsync()
	{
		if (!CanStopRpc())
		{
			return;
		}
		await _opGate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			_isStopping = true;
			UpdateCommands();
			SafeUpdateStatus("Stopping RPC...", Brushes.Orange);
			_reconnectTimer.IsEnabled = false;
			CancelAndDispose(ref _presenceCts);
			CancelAndDispose(ref _saveCts);
			StopClientIfRunning();
			_rpcConnected = false;
			SafeUpdateStatus("RPC Stopped", Brushes.Gray);
		}
		finally
		{
			_isStopping = false;
			UpdateCommands();
			_opGate.Release();
		}
	}

	private void StopClientIfRunning()
	{
		DiscordRpcClient? client;
		lock (_rpcLock)
		{
			client = _client;
			_client = null;
		}
		if (client != null)
		{
			ReleaseClient(client);
		}
	}

	private void OnClientReady(object sender, ReadyMessage e)
	{
		_rpcConnected = true;
		SafeUpdateStatus("Connected as " + e.User.Username, Brushes.LimeGreen);
		DispatcherInvokeSafe(UpdatePresence);
	}

	private void OnClientClose(object sender, CloseMessage e)
	{
		_rpcConnected = false;
		SafeUpdateStatus("Disconnected from Discord", Brushes.OrangeRed);
	}

	private void OnClientError(object sender, ErrorMessage e)
	{
		_rpcConnected = false;
		SafeUpdateStatus("Error: " + e.Message, Brushes.Red);
	}

	private void ReleaseClient(DiscordRpcClient client)
	{
		client.OnReady -= OnClientReady;
		client.OnClose -= OnClientClose;
		client.OnError -= OnClientError;
		try
		{
			client.ClearPresence();
		}
		catch
		{
		}
		try
		{
			client.Dispose();
		}
		catch
		{
		}
	}

	private static void ReplaceCancellation(ref CancellationTokenSource source)
	{
		CancellationTokenSource replacement = new CancellationTokenSource();
		CancellationTokenSource previous = source;
		source = replacement;
		if (previous != null)
		{
			previous.Cancel();
			previous.Dispose();
		}
	}

	private static void CancelAndDispose(ref CancellationTokenSource source)
	{
		CancellationTokenSource current = source;
		source = null;
		if (current != null)
		{
			current.Cancel();
			current.Dispose();
		}
	}

	private async Task SafeManualUpdateAsync()
	{
		if (_client != null && _rpcConnected)
		{
			SafeUpdateStatus("Manually updating presence...", Brushes.Orange);
			await _dispatcher.InvokeAsync((Action)UpdatePresence, (DispatcherPriority)4);
		}
	}

	private void UpdatePresence()
	{
		if (_client == null || !_rpcConnected)
		{
			return;
		}
		try
		{
			DiscordRPC.RichPresence richPresence = new DiscordRPC.RichPresence
			{
				Details = (string.IsNullOrWhiteSpace(Details) ? "Using Voidstrap" : Details),
				State = State,
				Assets = new Assets
				{
					LargeImageKey = (string.IsNullOrWhiteSpace(LargeImageKey) ? null : LargeImageKey),
					LargeImageText = AppName,
					SmallImageKey = (string.IsNullOrWhiteSpace(SmallImageKey) ? null : SmallImageKey),
					SmallImageText = "Voidstrap RPC"
				}
			};
			List<Button> list = BuildValidButtons();
			if (list.Count > 0)
			{
				richPresence.Buttons = list.ToArray();
			}
			lock (_rpcLock)
			{
				_client?.SetPresence(richPresence);
			}
			SafeUpdateStatus("Presence updated successfully", Brushes.LightSkyBlue);
		}
		catch (Exception ex)
		{
			SafeUpdateStatus("Presence update failed: " + ex.Message, Brushes.Red);
		}
	}

	private List<Button> BuildValidButtons()
	{
		List<Button> list = new List<Button>();
		if (Button1Enabled && Valid(Button1Url))
		{
			list.Add(new Button
			{
				Label = (string.IsNullOrWhiteSpace(Button1Label) ? "Link" : Button1Label),
				Url = Button1Url
			});
		}
		if (Button2Enabled && Valid(Button2Url))
		{
			list.Add(new Button
			{
				Label = (string.IsNullOrWhiteSpace(Button2Label) ? "Link" : Button2Label),
				Url = Button2Url
			});
		}
		return list;
		static bool Valid(string url)
		{
			if (string.IsNullOrWhiteSpace(url))
			{
				return false;
			}
			if (!Uri.TryCreate(url, UriKind.Absolute, out Uri result))
			{
				return false;
			}
			if (!(result.Scheme == Uri.UriSchemeHttps))
			{
				return result.Scheme == Uri.UriSchemeHttp;
			}
			return true;
		}
	}

	private async Task CheckReconnectAsync()
	{
		if (_isStopping || _isStarting)
		{
			return;
		}
		bool flag = false;
		lock (_rpcLock)
		{
			if (_client == null)
			{
				flag = true;
			}
		}
		if (flag || !_rpcConnected)
		{
			SafeUpdateStatus("Attempting reconnect...", Brushes.Orange);
			await SafeStartRpcAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private void SaveConfigInternal()
	{
		try
		{
			Voidstrap.Utility.JsonFile.SerializeAtomic(_configPath, new RpcConfig(_applicationId, _appName, _details, _state, _largeImageKey, _smallImageKey, _button1Enabled, _button2Enabled, _button1Label, _button1Url, _button2Label, _button2Url, _autoStartRpc), Voidstrap.Utility.JsonOptions.Indented);
			SafeUpdateStatus("Config Saved", Brushes.LightGreen);
		}
		catch (Exception ex)
		{
			SafeUpdateStatus("Save Failed: " + ex.Message, Brushes.Red);
		}
	}

	private void LoadConfigInternal()
	{
		if (!File.Exists(_configPath))
		{
			UpdateCommands();
			return;
		}
		try
		{
			_isLoadingConfig = true;
			RpcConfig rpcConfig = Voidstrap.Utility.JsonFile.Deserialize<RpcConfig>(_configPath, Voidstrap.Utility.JsonOptions.Tolerant, 4194304);
			if (!(rpcConfig == null))
			{
				ApplicationId = rpcConfig.ApplicationId;
				AppName = rpcConfig.AppName;
				Details = rpcConfig.Details;
				State = rpcConfig.State;
				LargeImageKey = rpcConfig.LargeImageKey;
				SmallImageKey = rpcConfig.SmallImageKey;
				Button1Enabled = rpcConfig.Button1Enabled;
				Button2Enabled = rpcConfig.Button2Enabled;
				Button1Label = rpcConfig.Button1Label;
				Button1Url = rpcConfig.Button1Url;
				Button2Label = rpcConfig.Button2Label;
				Button2Url = rpcConfig.Button2Url;
				AutoStartRpc = rpcConfig.AutoStartRpc;
				DispatcherInvokeSafe(delegate
				{
					this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
				});
				UpdateCommands();
				SafeUpdateStatus("Config Loaded", Brushes.LightBlue);
				if (AutoStartRpc && !string.IsNullOrWhiteSpace(ApplicationId))
				{
					SafeUpdateStatus("Auto-starting RPC...", Brushes.DarkOrange);
					SafeStartRpcAsync();
				}
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to load config: " + ex.Message);
		}
		finally
		{
			_isLoadingConfig = false;
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		try
		{
			SaveConfigInternal();
		}
		catch
		{
		}
		_reconnectTimer.Stop();
		_reconnectTimer.Tick -= OnReconnectTimerTick;
		AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
		CancelAndDispose(ref _presenceCts);
		CancelAndDispose(ref _saveCts);
		StopClientIfRunning();
		_rpcConnected = false;
		PropertyChanged = null;
		if (ReferenceEquals(_shared, this))
		{
			_shared = null;
		}
		try
		{
			_opGate.Dispose();
		}
		catch
		{
		}
		GC.SuppressFinalize(this);
	}

	protected bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return false;
		}
		field = value;
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		return true;
	}
}

