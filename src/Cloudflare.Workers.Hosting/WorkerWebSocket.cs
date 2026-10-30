using Cloudflare.Workers.Hosting.Interop;

namespace Cloudflare.Workers.Hosting;

/// <summary>A Workers WebSocket obtained from a fetch upgrade response.</summary>
public sealed class WorkerWebSocket : IDisposable
{
    private readonly JsObject _js;
    private readonly Queue<JsObject> _messages = [];
    private readonly Queue<TaskCompletionSource<JsObject>> _waiting = [];
    private readonly int _onMessageId;
    private readonly int _onCloseId;
    private readonly int _onErrorId;
    private readonly JsObject? _onMessage;
    private readonly JsObject? _onClose;
    private readonly JsObject? _onError;
    private Exception? _failure;
    private bool _listeningMessage;
    private bool _listeningClose;
    private bool _listeningError;
    private bool _disposed;

    internal WorkerWebSocket(JsObject js)
    {
        _js = js;
        try
        {
            _onMessageId = CallbackRegistry.Register(OnMessage);
            _onMessage = js.Runtime.NewCallback(_onMessageId);
            _onCloseId = CallbackRegistry.Register(OnClose);
            _onClose = js.Runtime.NewCallback(_onCloseId);
            _onErrorId = CallbackRegistry.Register(_ => Fail(new JsException("WebSocket error")));
            _onError = js.Runtime.NewCallback(_onErrorId);
            // Listen before accept() so early CDP messages are buffered.
            AddListener("message", _onMessage);
            _listeningMessage = true;
            AddListener("close", _onClose);
            _listeningClose = true;
            AddListener("error", _onError);
            _listeningError = true;
        }
        catch
        {
            try
            {
                DetachListeners();
            }
            finally
            {
                js.Dispose();
            }
            throw;
        }
    }

    public JsObject Js => _js;

    public void Accept()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Keep binary messages readable through JsObject.AsBytes() on all compatibility dates.
        _js.SetProperty("binaryType", JsArg.From("arraybuffer"));
        using var result = _js.Call("accept");
    }

    public void Send(string message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var result = _js.Call("send", JsArg.From(message));
    }

    public void Send(byte[] message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var result = _js.Call("send", JsArg.From(message));
    }

    /// <summary>
    /// Receives the next message in order. The caller must dispose the returned value.
    /// Use AsString() for text and AsBytes() for binary messages. Closure or errors throw JsException.
    /// </summary>
    public Task<JsObject> ReceiveAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_messages.TryDequeue(out var message))
        {
            return Task.FromResult(message);
        }
        if (_failure is not null)
        {
            return Task.FromException<JsObject>(_failure);
        }

        var completion = new TaskCompletionSource<JsObject>();
        _waiting.Enqueue(completion);
        return completion.Task;
    }

    public void Close(int code = 1000, string reason = "")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var result = _js.Call("close", JsArg.From(code), JsArg.From(reason));
    }

    private void OnMessage(JsHandle handle)
    {
        if (_failure is not null || _disposed)
        {
            return;
        }

        using var args = _js.Runtime.WrapToJsObject(_js.Runtime.Interop.Clone(handle));
        using var evt = args.GetElement(0);
        var message = evt.GetProperty("data");
        if (_waiting.TryDequeue(out var completion))
        {
            completion.SetResult(message);
        }
        else
        {
            _messages.Enqueue(message);
        }
    }

    private void OnClose(JsHandle handle)
    {
        using var args = _js.Runtime.WrapToJsObject(_js.Runtime.Interop.Clone(handle));
        using var evt = args.GetElement(0);
        var exception = new JsException($"WebSocket closed ({evt.GetPropertyAsNumber("code")}): {evt.GetPropertyAsString("reason") ?? string.Empty}");
        try
        {
            // Older compatibility settings require us to complete the closing handshake.
            if (_js.GetPropertyAsNumber("readyState") == 2)
            {
                // Omitting the status also handles a peer's Close frame with no status code.
                using var result = _js.Call("close");
            }
        }
        finally
        {
            Fail(exception);
        }
    }

    private void Fail(Exception exception)
    {
        if (_failure is not null)
        {
            return;
        }

        _failure = exception;
        try
        {
            DetachListeners();
        }
        finally
        {
            while (_waiting.TryDequeue(out var completion))
            {
                completion.SetException(exception);
            }
        }
    }

    private void AddListener(string type, JsObject callback)
    {
        using var result = _js.Call("addEventListener", JsArg.From(type), JsArg.From(callback));
    }

    private void RemoveListener(string type, JsObject callback)
    {
        using var result = _js.Call("removeEventListener", JsArg.From(type), JsArg.From(callback));
    }

    private void DetachListeners()
    {
        try
        {
            if (_listeningMessage)
            {
                _listeningMessage = false;
                RemoveListener("message", _onMessage!);
            }
            if (_listeningClose)
            {
                _listeningClose = false;
                RemoveListener("close", _onClose!);
            }
            if (_listeningError)
            {
                _listeningError = false;
                RemoveListener("error", _onError!);
            }
        }
        finally
        {
            CallbackRegistry.Unregister(_onMessageId);
            CallbackRegistry.Unregister(_onCloseId);
            CallbackRegistry.Unregister(_onErrorId);
            _onMessage?.Dispose();
            _onClose?.Dispose();
            _onError?.Dispose();
        }
    }

    /// <summary>Closes the socket, removes listeners, and releases its handles.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            Fail(new JsException("WebSocket disposed"));
            if (_js.GetPropertyAsNumber("readyState") == 1)
            {
                using var result = _js.Call("close", JsArg.From(1000), JsArg.From(string.Empty));
            }
        }
        finally
        {
            while (_messages.TryDequeue(out var message))
            {
                message.Dispose();
            }
            _js.Dispose();
        }
    }
}
