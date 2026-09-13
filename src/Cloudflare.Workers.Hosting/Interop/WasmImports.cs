using System.Runtime.InteropServices;

namespace Cloudflare.Workers.Hosting.Interop;

#pragma warning disable SYSLIB1054

internal static unsafe class WasmImports
{
    private const string Module = "cf";

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "string_new")]
    internal static extern int StringNew(byte* utf8, int length);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "string_utf8_length")]
    internal static extern int StringUtf8Length(int handle);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "string_read")]
    internal static extern void StringRead(int handle, byte* destination);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "number_new")]
    internal static extern int NumberNew(double value);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "number_value")]
    internal static extern double NumberValue(int handle);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "boolean_new")]
    internal static extern int BooleanNew(int value);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "boolean_value")]
    internal static extern int BooleanValue(int handle);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "value_kind")]
    internal static extern int ValueKind(int handle);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "clone")]
    internal static extern int Clone(int handle);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "release_handle")]
    internal static extern void ReleaseHandle(int handle);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "object_new")]
    internal static extern int ObjectNew();

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "array_new")]
    internal static extern int ArrayNew();

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "array_length")]
    internal static extern int ArrayLength(int handle);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "array_get")]
    internal static extern int ArrayGet(int handle, int index);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "array_push")]
    internal static extern void ArrayPush(int array, int value);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "global_get")]
    internal static extern int GlobalGet(byte* nameUtf8, int nameLength);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "property_get")]
    internal static extern int PropertyGet(int target, byte* nameUtf8, int nameLength);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "property_set")]
    internal static extern void PropertySet(int target, byte* nameUtf8, int nameLength, int value);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "call_method")]
    internal static extern int CallMethod(int target, byte* nameUtf8, int nameLength, int argsArray, int* result);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "call_function")]
    internal static extern int CallFunction(int function, int thisArg, int argsArray, int* result);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "construct")]
    internal static extern int Construct(int constructor, int argsArray, int* result);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "bytes_new")]
    internal static extern int BytesNew(byte* data, int length);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "bytes_length")]
    internal static extern int BytesLength(int handle);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "bytes_read")]
    internal static extern void BytesRead(int handle, byte* destination);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "promise_register_continuation")]
    internal static extern void PromiseRegisterContinuation(int promise, int continuationId);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "promise_new")]
    internal static extern int PromiseNew();

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "promise_get")]
    internal static extern int PromiseGet(int promiseId);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "promise_resolve")]
    internal static extern void PromiseResolve(int promiseId, int value);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "promise_reject")]
    internal static extern void PromiseReject(int promiseId, byte* messageUtf8, int messageLength);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "set_timeout")]
    internal static extern void SetTimeout(int continuationId, double milliseconds);

    [WasmImportLinkage]
    [DllImport(Module, EntryPoint = "log")]
    internal static extern void Log(int level, byte* messageUtf8, int messageLength);
}
