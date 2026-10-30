using Cloudflare.Workers.Hosting.Interop;
using Cloudflare.Workers.Hosting.Tests.Fakes;
using Xunit;

namespace Cloudflare.Workers.Hosting.Tests;

public class CallbackRegistryTests
{
    [Fact]
    public void Callback_ReceivesMultipleArgumentsOnEveryInvocation()
    {
        var (interop, runtime) = FakeWorld.Create();
        var received = new List<(string, double)>();
        int callbackId = CallbackRegistry.Register(handle =>
        {
            using var args = runtime.WrapToJsObject(interop.Clone(handle));
            Assert.Equal(2, args.GetArrayLength());
            using var name = args.GetElement(0);
            using var number = args.GetElement(1);
            received.Add((name.AsString(), number.AsNumber()));
        });
        try
        {
            using var callback = runtime.NewCallback(callbackId);
            Assert.Equal(JsValueKind.Function, callback.Kind);
            using var first = callback.InvokeAsFunction(null, JsArg.From("first"), JsArg.From(1));
            using var second = callback.InvokeAsFunction(null, JsArg.From("second"), JsArg.From(2));
        }
        finally
        {
            CallbackRegistry.Unregister(callbackId);
        }
        Assert.Equal(new[] { ("first", 1d), ("second", 2d) }, received);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public void Callback_FunctionHandleAndRegistrationHaveIndependentLifetimes()
    {
        var (interop, runtime) = FakeWorld.Create();
        int calls = 0;
        int callbackId = CallbackRegistry.Register(handle =>
        {
            using var args = runtime.WrapToJsObject(interop.Clone(handle));
            Assert.Equal(0, args.GetArrayLength());
            calls++;
        });
        try
        {
            using var holder = runtime.NewObject();
            using var callback = runtime.NewCallback(callbackId);
            holder.SetProperty("onChange", JsArg.From(callback));
            using var retained = holder.GetProperty("onChange");
            using var first = retained.InvokeAsFunction(null);
            callback.Dispose();
            callback.Dispose();
            using var second = retained.InvokeAsFunction(null);
            Assert.Equal(2, calls);

            CallbackRegistry.Unregister(callbackId);
            using var late = retained.InvokeAsFunction(null);
            Assert.Equal(2, calls);
        }
        finally
        {
            CallbackRegistry.Unregister(callbackId);
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public void Callback_ArgumentsCanBeRetainedByOwner()
    {
        var (interop, runtime) = FakeWorld.Create();
        JsObject? retained = null;
        int callbackId = CallbackRegistry.Register(handle =>
            retained = runtime.WrapToJsObject(interop.Clone(handle)));
        try
        {
            using var callback = runtime.NewCallback(callbackId);
            using var result = callback.InvokeAsFunction(null, JsArg.From("retained"));
        }
        finally
        {
            CallbackRegistry.Unregister(callbackId);
        }
        using (retained!)
        using (var value = retained!.GetElement(0))
        {
            Assert.Equal("retained", value.AsString());
        }
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public void Callback_ExceptionsReleaseBorrowedArgumentsAndLeaveRegistrationActive()
    {
        var (interop, runtime) = FakeWorld.Create();
        int calls = 0;
        int callbackId = CallbackRegistry.Register(_ =>
        {
            calls++;
            throw new InvalidOperationException("callback failure");
        });
        try
        {
            using var callback = runtime.NewCallback(callbackId);
            for (int i = 0; i < 2; i++)
            {
                Assert.Throws<InvalidOperationException>(() => callback.InvokeAsFunction(null, JsArg.From(i)));
                Assert.Equal(1, interop.LiveHandleCount);
            }
        }
        finally
        {
            CallbackRegistry.Unregister(callbackId);
        }
        Assert.Equal(2, calls);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public void Registry_AllowsUnregisteringDuringInvocationAndPreservesBorrowedHandle()
    {
        var (interop, runtime) = FakeWorld.Create();
        int calls = 0;
        int callbackId = 0;
        callbackId = CallbackRegistry.Register(_ =>
        {
            calls++;
            CallbackRegistry.Unregister(callbackId);
        });
        try
        {
            using var args = runtime.NewArray(JsArg.From("borrowed"));
            CallbackRegistry.Invoke(callbackId, args.Handle);
            CallbackRegistry.Invoke(callbackId, args.Handle);
            using var value = args.GetElement(0);
            Assert.Equal("borrowed", value.AsString());
        }
        finally
        {
            CallbackRegistry.Unregister(callbackId);
        }
        Assert.Equal(1, calls);
        Assert.Equal(0, interop.LiveHandleCount);
    }

    [Fact]
    public void Registry_ForwardsHandlesRepeatedlyUntilUnregistered()
    {
        var received = new List<JsHandle>();
        int callbackId = CallbackRegistry.Register(received.Add);
        try
        {
            CallbackRegistry.Invoke(callbackId, new JsHandle(100));
            CallbackRegistry.Invoke(callbackId, new JsHandle(200));
            CallbackRegistry.Unregister(callbackId);
            CallbackRegistry.Invoke(callbackId, new JsHandle(300));

            Assert.Equal(new[] { new JsHandle(100), new JsHandle(200) }, received);
        }
        finally
        {
            CallbackRegistry.Unregister(callbackId);
        }
    }
}
