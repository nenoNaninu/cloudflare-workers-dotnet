namespace Cloudflare.Workers.Hosting.Interop;

public interface IJsInterop
{
    // -- primitive values ---------------------------------------------------
    JsHandle StringNew(string value);
    string StringGet(JsHandle handle);
    JsHandle NumberNew(double value);
    double NumberValue(JsHandle handle);
    JsHandle BooleanNew(bool value);
    bool BooleanValue(JsHandle handle);
    JsValueKind GetKind(JsHandle handle);

    // -- handle lifetime ----------------------------------------------------
    JsHandle Clone(JsHandle handle);
    void ReleaseHandle(JsHandle handle);

    // -- objects and arrays -------------------------------------------------
    JsHandle ObjectNew();
    JsHandle ArrayNew();
    int ArrayLength(JsHandle array);
    JsHandle ArrayGet(JsHandle array, int index);
    void ArrayPush(JsHandle array, JsHandle handle);

    JsHandle GetGlobal(string name);
    JsHandle GetProperty(JsHandle target, string name);
    void SetProperty(JsHandle target, string name, JsHandle handle);

    // -- function / method calling ------------------------------------------

    bool TryCallMethod(JsHandle target, string name, JsHandle argsArray, out JsHandle result);
    bool TryCallFunction(JsHandle function, JsHandle thisArg, JsHandle argsArray, out JsHandle result);
    bool TryConstruct(JsHandle constructor, JsHandle argsArray, out JsHandle result);

    // -- binary data ----------------------------------------------------------
    JsHandle BytesNew(ReadOnlySpan<byte> bytes);
    int BytesLength(JsHandle handle);
    void BytesRead(JsHandle handle, Span<byte> destination);

    // -- async ----------------------------------------------------------------
    void PromiseRegisterCallback(JsHandle promise, int callbackId);

    PromiseId PromiseNew();
    JsHandle PromiseGet(PromiseId promiseId);
    void PromiseResolve(PromiseId promiseId, JsHandle handle);
    void PromiseReject(PromiseId promiseId, string message);

    void SetTimeout(int callbackId, double milliseconds);

    // -- diagnostics ----------------------------------------------------------
    void Log(ConsoleLevel level, string message);
}
