using System.IO;
using HuaweiPods.Bluetooth;
using HuaweiPods.Protocol;

namespace HuaweiPods;

internal sealed class HeadsetSession(HuaweiRfcommConnection connection) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public Task WriteAsync(byte[] packet)
    {
        return WriteWithTimeoutAsync(packet, _lifetime.Token);
    }

    private async Task WriteWithTimeoutAsync(byte[] packet, CancellationToken lifetime)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await connection.WriteAsync(packet, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("向耳机发送数据超时。");
        }
    }

    public async Task ReadAsync(Action<HuaweiFrame> onFrame, Action<Exception> onDisconnected)
    {
        CancellationToken lifetime = _lifetime.Token;
        var decoder = new HuaweiFrameDecoder();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                int length = await connection.ReadAsync(buffer, lifetime).ConfigureAwait(false);
                if (length == 0)
                    throw new IOException("耳机已关闭控制连接。");

                foreach (HuaweiFrame frame in decoder.Push(buffer.AsSpan(0, length)))
                    onFrame(frame);
            }
        }
        catch (Exception error)
        {
            if (!lifetime.IsCancellationRequested)
                onDisconnected(error);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetime.Cancel();
        connection.Dispose();
        _lifetime.Dispose();
    }
}
