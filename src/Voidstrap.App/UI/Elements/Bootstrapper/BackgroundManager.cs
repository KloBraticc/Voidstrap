using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfAnimatedGif;

namespace Voidstrap.UI.Elements.Bootstrapper;

public static class BackgroundManager
{
    private sealed class LoadState
    {
        public readonly object Sync = new();
        public CancellationTokenSource? Cancellation;
        public int Generation;
    }

    private static readonly ConditionalWeakTable<Image, LoadState> States = new();
    private const int MaxWidth = 1920;
    private const long MaxGifBytes = 64L * 1024 * 1024;

    public static async Task SetBackgroundAsync(Image imageControl, string? customPath)
    {
        if (imageControl == null)
        {
            return;
        }
        LoadState state = States.GetOrCreateValue(imageControl);
        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous;
        int generation;
        lock (state.Sync)
        {
            previous = state.Cancellation;
            state.Cancellation = cancellation;
            generation = ++state.Generation;
        }
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            ApplyHighQualityScaling(imageControl);
            if (string.IsNullOrWhiteSpace(customPath) || !File.Exists(customPath))
            {
                await ClearBackgroundAsync(imageControl, state, generation, cancellation.Token);
                return;
            }
            if (Path.GetExtension(customPath).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                await LoadGifAsync(imageControl, customPath, state, generation, cancellation.Token);
            }
            else
            {
                await LoadStaticImageAsync(imageControl, customPath, state, generation, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("BackgroundManager", "Background load failed: " + ex.Message);
            await ClearBackgroundAsync(imageControl, state, generation, CancellationToken.None);
        }
        finally
        {
            lock (state.Sync)
            {
                if (ReferenceEquals(state.Cancellation, cancellation))
                {
                    state.Cancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    public static void Cancel(Image? imageControl)
    {
        if (imageControl == null || !States.TryGetValue(imageControl, out LoadState? state))
        {
            return;
        }
        CancellationTokenSource? cancellation;
        lock (state.Sync)
        {
            cancellation = state.Cancellation;
            state.Cancellation = null;
            state.Generation++;
        }
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (imageControl.Dispatcher.CheckAccess())
        {
            ClearImage(imageControl);
        }
        else
        {
            imageControl.Dispatcher.BeginInvoke((Action)(() => ClearImage(imageControl)));
        }
    }

    private static async Task LoadGifAsync(Image imageControl, string path, LoadState state, int generation, CancellationToken token)
    {
        FileInfo info = new(path);
        if (info.Length <= 0 || info.Length > MaxGifBytes)
        {
            throw new InvalidDataException("The animated background is too large.");
        }
        byte[] data = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        BitmapImage bitmap = new();
        using (MemoryStream stream = new(data, writable: false))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.DecodePixelWidth = MaxWidth;
            bitmap.EndInit();
        }
        bitmap.Freeze();
        await imageControl.Dispatcher.InvokeAsync((Action)delegate
        {
            if (!IsCurrent(state, generation, token))
            {
                return;
            }
            ClearImage(imageControl);
            if (Voidstrap.Utility.Platform.IsWindows)
            {
                ImageBehavior.SetAnimatedSource(imageControl, bitmap);
                ImageBehavior.SetRepeatBehavior(imageControl, RepeatBehavior.Forever);
            }
            else
            {
                imageControl.Source = bitmap;
            }
            ApplyHighQualityScaling(imageControl);
        }, DispatcherPriority.Render, token);
    }

    private static async Task LoadStaticImageAsync(Image imageControl, string path, LoadState state, int generation, CancellationToken token)
    {
        BitmapSource? bitmap = await Task.Run(() => Voidstrap.Utility.SafeImaging.FromFile(path, MaxWidth), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (bitmap == null)
        {
            throw new InvalidDataException("The background image could not be decoded.");
        }
        if (bitmap.CanFreeze && !bitmap.IsFrozen)
        {
            bitmap.Freeze();
        }
        await imageControl.Dispatcher.InvokeAsync((Action)delegate
        {
            if (!IsCurrent(state, generation, token))
            {
                return;
            }
            ClearImage(imageControl);
            imageControl.Source = bitmap;
            ApplyHighQualityScaling(imageControl);
        }, DispatcherPriority.Render, token);
    }

    private static Task ClearBackgroundAsync(Image imageControl, LoadState state, int generation, CancellationToken token)
    {
        return imageControl.Dispatcher.InvokeAsync((Action)delegate
        {
            if (IsCurrent(state, generation, token))
            {
                ClearImage(imageControl);
            }
        }, DispatcherPriority.Render, token).Task;
    }

    private static bool IsCurrent(LoadState state, int generation, CancellationToken token)
    {
        lock (state.Sync)
        {
            return !token.IsCancellationRequested && state.Generation == generation;
        }
    }

    private static void ClearImage(Image imageControl)
    {
        ImageBehavior.SetAnimatedSource(imageControl, null);
        imageControl.Source = null;
    }

    private static void ApplyHighQualityScaling(Image imageControl)
    {
        RenderOptions.SetBitmapScalingMode(imageControl, BitmapScalingMode.HighQuality);
        imageControl.SnapsToDevicePixels = true;
        imageControl.UseLayoutRounding = true;
    }
}
