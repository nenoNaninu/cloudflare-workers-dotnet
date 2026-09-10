namespace Cloudflare.Workers.Hosting.Tests.Fakes;

/// <summary>Sentinel for JavaScript <c>undefined</c> (C# <c>null</c> models JS <c>null</c>).</summary>
public sealed class JsUndefined
{
    public static readonly JsUndefined Value = new();

    private JsUndefined()
    {
    }

    public override string ToString() => "undefined";
}

/// <summary>A JS object: property bag keyed by string.</summary>
public sealed class FakeObject : Dictionary<string, object?>;

/// <summary>A JS array.</summary>
public sealed class FakeArray : List<object?>
{
    public FakeArray()
    {
    }

    public FakeArray(IEnumerable<object?> items)
        : base(items)
    {
    }
}

/// <summary>A callable JS function: (thisArg, args) => result.</summary>
public sealed class FakeFunction(Func<object?, object?[], object?> body)
{
    public object? Invoke(object? thisArg, object?[] args) => body(thisArg, args);
}

/// <summary>A JS constructor function (for <c>new X(...)</c>).</summary>
public sealed class FakeConstructor(Func<object?[], object?> body)
{
    public object? Construct(object?[] args) => body(args);
}

/// <summary>Thrown inside fake JS code to model a JS exception.</summary>
public sealed class FakeJsThrow(object? error) : Exception(error?.ToString() ?? "js error")
{
    public object? Error { get; } = error;

    public static FakeJsThrow WithMessage(string message)
        => new(new FakeObject { ["message"] = message });
}

/// <summary>A JS promise with test-controllable settlement.</summary>
public sealed class FakePromise
{
    public enum PromiseState
    {
        Pending,
        Fulfilled,
        Rejected,
    }

    private readonly List<Action<bool, object?>> _callbacks = [];

    public PromiseState State { get; private set; } = PromiseState.Pending;

    public object? Value { get; private set; }

    public static FakePromise Resolved(object? value)
    {
        var promise = new FakePromise();
        promise.Resolve(value);
        return promise;
    }

    public static FakePromise Rejected(object? error)
    {
        var promise = new FakePromise();
        promise.Reject(error);
        return promise;
    }

    public void Resolve(object? value) => Settle(PromiseState.Fulfilled, value);

    public void Reject(object? error) => Settle(PromiseState.Rejected, error);

    public void OnSettled(Action<bool, object?> callback)
    {
        if (State == PromiseState.Pending)
        {
            _callbacks.Add(callback);
        }
        else
        {
            callback(State == PromiseState.Fulfilled, Value);
        }
    }

    private void Settle(PromiseState state, object? value)
    {
        if (State != PromiseState.Pending)
        {
            throw new InvalidOperationException("Promise already settled.");
        }

        State = state;
        Value = value;
        var callbacks = _callbacks.ToArray();
        _callbacks.Clear();
        foreach (var callback in callbacks)
        {
            callback(state == PromiseState.Fulfilled, value);
        }
    }
}
