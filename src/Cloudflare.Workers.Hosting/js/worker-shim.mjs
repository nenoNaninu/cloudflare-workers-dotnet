// Cloudflare.Workers.Hosting JS shim.
//
// Hosts a .NET NativeAOT-LLVM (wasi-wasm) reactor module inside the Cloudflare
// Workers runtime — the counterpart of the wasm-bindgen glue used by workers-rs.
//
// Responsibilities:
//   * hold JS values in a handle table, addressed by int32 handles from C#
//   * implement the "cf" wasm import namespace (see IJsInterop)
//   * implement the minimal WASI preview1 surface .NET needs (clock, random,
//     stdout -> console, ...) since workerd provides no WASI
//   * bridge `export default { fetch, scheduled }` to the wasm exports

import wasmModule from "./app.wasm";

// --------------------------------------------------------------------------
// Handle table. Slots 0-3 are reserved singletons and are never released.
// --------------------------------------------------------------------------

const heapSlot = [undefined, null, true, false];
const freeSlots = [];

function holdToHeap(value) {
  const slot = freeSlots.length > 0 ? freeSlots.pop() : heapSlot.length;
  heapSlot[slot] = value;
  return slot;
}

function getFromHeap(handle) {
  return heapSlot[handle];
}

function releaseFromHeap(handle) {
  if (handle < 4) {
    return;
  }
  heapSlot[handle] = undefined;
  freeSlots.push(handle);
}

// --------------------------------------------------------------------------
// Instance state
// --------------------------------------------------------------------------

let instance = null;

function exportsOf() {
  return instance.exports;
}

function memoryBytes() {
  return new Uint8Array(instance.exports.memory.buffer);
}

function memoryView() {
  return new DataView(instance.exports.memory.buffer);
}

const textDecoder = new TextDecoder("utf-8");
const textEncoder = new TextEncoder();

function readString(ptr, len) {
  if (len === 0) {
    return "";
  }
  return textDecoder.decode(new Uint8Array(instance.exports.memory.buffer, ptr, len));
}

// --------------------------------------------------------------------------
// Promises: shim-side promises resolvable from C#.
// --------------------------------------------------------------------------

let nextPromiseId = 1;
const promises = new Map();

function createPromise() {
  const id = nextPromiseId++;
  let resolve, reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  promises.set(id, { promise, resolve, reject });
  return id;
}

// --------------------------------------------------------------------------
// "cf" import namespace — the shim half of IJsInterop.
// --------------------------------------------------------------------------

// Bytes are cached between the string_utf8_length / string_read call pair.
const encodedStrings = new Map();

function asUint8Array(value) {
  if (value instanceof Uint8Array) {
    return value;
  }
  if (value instanceof ArrayBuffer) {
    return new Uint8Array(value);
  }
  if (ArrayBuffer.isView(value)) {
    return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
  }
  throw new Error("value is not binary data");
}

function kindOf(value) {
  if (value === undefined) {
    return 0;
  }
  if (value === null) {
    return 1;
  }
  switch (typeof value) {
    case "boolean": return 2;
    case "number": return 3;
    case "string": return 4;
    case "bigint": return 5;
    case "symbol": return 6;
    case "function": return 7;
    default: return 8;
  }
}

const cfImports = {
  string_new: (ptr, len) => holdToHeap(readString(ptr, len)),
  string_utf8_length: (h) => {
    const bytes = textEncoder.encode(getFromHeap(h));
    encodedStrings.set(h, bytes);
    return bytes.length;
  },
  string_read: (h, ptr) => {
    const bytes = encodedStrings.get(h) ?? textEncoder.encode(getFromHeap(h));
    encodedStrings.delete(h);
    memoryBytes().set(bytes, ptr);
  },

  number_new: (value) => holdToHeap(value),
  number_value: (h) => Number(getFromHeap(h)),
  boolean_new: (value) => (value !== 0 ? 2 : 3),
  boolean_value: (h) => (getFromHeap(h) ? 1 : 0),
  value_kind: (h) => kindOf(getFromHeap(h)),

  clone: (h) => holdToHeap(getFromHeap(h)),
  release_handle: (h) => releaseFromHeap(h),

  object_new: () => holdToHeap({}),
  array_new: () => holdToHeap([]),
  array_length: (h) => getFromHeap(h).length,
  array_get: (h, index) => holdToHeap(getFromHeap(h)[index]),
  array_push: (arrayH, valueH) => { getFromHeap(arrayH).push(getFromHeap(valueH)); },

  global_get: (namePtr, nameLen) => holdToHeap(globalThis[readString(namePtr, nameLen)]),
  property_get: (targetH, namePtr, nameLen) => {
    try {
      const target = getFromHeap(targetH);
      if (target === undefined || target === null) {
        return 0;
      }
      return holdToHeap(target[readString(namePtr, nameLen)]);
    } catch {
      return 0;
    }
  },
  property_set: (targetH, namePtr, nameLen, valueH) => {
    getFromHeap(targetH)[readString(namePtr, nameLen)] = getFromHeap(valueH);
  },

  call_method: (targetH, namePtr, nameLen, argsH, outPtr) => {
    try {
      const target = getFromHeap(targetH);
      const name = readString(namePtr, nameLen);
      const method = target?.[name];
      if (typeof method !== "function") {
        throw new TypeError(`${name} is not a function`);
      }
      const result = method.apply(target, getFromHeap(argsH));
      memoryView().setInt32(outPtr, holdToHeap(result), true);
      return 0;
    } catch (error) {
      memoryView().setInt32(outPtr, holdToHeap(error), true);
      return 1;
    }
  },
  call_function: (functionH, thisH, argsH, outPtr) => {
    try {
      const result = getFromHeap(functionH).apply(getFromHeap(thisH), getFromHeap(argsH));
      memoryView().setInt32(outPtr, holdToHeap(result), true);
      return 0;
    } catch (error) {
      memoryView().setInt32(outPtr, holdToHeap(error), true);
      return 1;
    }
  },
  construct: (constructorH, argsH, outPtr) => {
    try {
      const result = Reflect.construct(getFromHeap(constructorH), getFromHeap(argsH));
      memoryView().setInt32(outPtr, holdToHeap(result), true);
      return 0;
    } catch (error) {
      memoryView().setInt32(outPtr, holdToHeap(error), true);
      return 1;
    }
  },

  bytes_new: (ptr, len) => {
    const copy = new Uint8Array(len);
    copy.set(new Uint8Array(instance.exports.memory.buffer, ptr, len));
    return holdToHeap(copy);
  },
  bytes_length: (h) => asUint8Array(getFromHeap(h)).byteLength,
  bytes_read: (h, ptr) => {
    memoryBytes().set(asUint8Array(getFromHeap(h)), ptr);
  },

  promise_register_callback: (promiseH, callbackId) => {
    Promise.resolve(getFromHeap(promiseH)).then(
      (value) => exportsOf().cf_promise_complete(callbackId, 1, holdToHeap(value)),
      (error) => exportsOf().cf_promise_complete(callbackId, 0, holdToHeap(error)),
    );
  },

  promise_new: () => createPromise(),
  promise_get: (id) => holdToHeap(promises.get(id).promise),
  promise_resolve: (id, valueH) => {
    const promise = promises.get(id);
    promises.delete(id);
    promise?.resolve(getFromHeap(valueH));
    releaseFromHeap(valueH);
  },
  promise_reject: (id, msgPtr, msgLen) => {
    const promise = promises.get(id);
    promises.delete(id);
    promise?.reject(new Error(readString(msgPtr, msgLen)));
  },

  set_timeout: (callbackId, milliseconds) => {
    setTimeout(() => exportsOf().cf_timeout_fired(callbackId), milliseconds);
  },

  log: (level, ptr, len) => {
    const message = readString(ptr, len);
    switch (level) {
      case 0: console.debug(message); break;
      case 1: console.log(message); break;
      case 2: console.warn(message); break;
      default: console.error(message); break;
    }
  },
};

// --------------------------------------------------------------------------
// Minimal WASI preview1 host. .NET's wasi-libc startup probes a handful of
// syscalls; everything not implemented answers ERRNO_NOSYS (52).
// Spec (preview1 was removed from the WASI repo's main branch in Dec 2025;
// this is a permalink to the last revision):
// https://github.com/WebAssembly/WASI/blob/1ad15f399e45126d43b2f4ee5d5d2432d1d29b7b/legacy/preview1/docs.md
// --------------------------------------------------------------------------

const ERRNO_SUCCESS = 0;
const ERRNO_BADF = 8;
const ERRNO_NOSYS = 52;

class ProcExit extends Error {
  constructor(code) {
    super(`WASI proc_exit(${code})`);
    this.code = code;
  }
}

// Line-buffered stdout/stderr forwarded to the console.
const stdBuffers = { 1: "", 2: "" };

function flushStd(fd, force) {
  let buffer = stdBuffers[fd];
  let index;
  while ((index = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, index);
    (fd === 2 ? console.error : console.log)(line);
    buffer = buffer.slice(index + 1);
  }
  if (force && buffer.length > 0) {
    (fd === 2 ? console.error : console.log)(buffer);
    buffer = "";
  }
  stdBuffers[fd] = buffer;
}

const wasiImports = {
  args_sizes_get: (argcPtr, argvBufSizePtr) => {
    const view = memoryView();
    view.setUint32(argcPtr, 1, true);
    view.setUint32(argvBufSizePtr, 7, true);
    return ERRNO_SUCCESS;
  },
  args_get: (argvPtr, argvBufPtr) => {
    const view = memoryView();
    view.setUint32(argvPtr, argvBufPtr, true);
    memoryBytes().set(textEncoder.encode("worker\0"), argvBufPtr);
    return ERRNO_SUCCESS;
  },
  environ_sizes_get: (countPtr, bufSizePtr) => {
    const view = memoryView();
    view.setUint32(countPtr, 0, true);
    view.setUint32(bufSizePtr, 0, true);
    return ERRNO_SUCCESS;
  },
  environ_get: () => ERRNO_SUCCESS,

  clock_res_get: (_id, resultPtr) => {
    memoryView().setBigUint64(resultPtr, 1000000n, true);
    return ERRNO_SUCCESS;
  },
  clock_time_get: (_id, _precision, resultPtr) => {
    memoryView().setBigUint64(resultPtr, BigInt(Date.now()) * 1000000n, true);
    return ERRNO_SUCCESS;
  },

  random_get: (ptr, len) => {
    const bytes = memoryBytes();
    for (let offset = 0; offset < len; offset += 65536) {
      crypto.getRandomValues(bytes.subarray(ptr + offset, ptr + Math.min(len, offset + 65536)));
    }
    return ERRNO_SUCCESS;
  },

  fd_write: (fd, iovsPtr, iovsLen, nwrittenPtr) => {
    if (fd !== 1 && fd !== 2) {
      return ERRNO_BADF;
    }
    const view = memoryView();
    let written = 0;
    let text = "";
    for (let i = 0; i < iovsLen; i++) {
      const ptr = view.getUint32(iovsPtr + i * 8, true);
      const len = view.getUint32(iovsPtr + i * 8 + 4, true);
      text += readString(ptr, len);
      written += len;
    }
    stdBuffers[fd] += text;
    flushStd(fd, false);
    view.setUint32(nwrittenPtr, written, true);
    return ERRNO_SUCCESS;
  },
  fd_read: (_fd, _iovsPtr, _iovsLen, nreadPtr) => {
    memoryView().setUint32(nreadPtr, 0, true);
    return ERRNO_SUCCESS;
  },
  fd_close: () => ERRNO_SUCCESS,
  fd_seek: () => ERRNO_BADF,
  fd_fdstat_get: (fd, statPtr) => {
    if (fd > 2) {
      return ERRNO_BADF;
    }
    memoryBytes().fill(0, statPtr, statPtr + 24);
    memoryView().setUint8(statPtr, 2); // filetype: character_device
    return ERRNO_SUCCESS;
  },
  fd_fdstat_set_flags: () => ERRNO_SUCCESS,
  fd_prestat_get: () => ERRNO_BADF, // no preopened directories
  fd_prestat_dir_name: () => ERRNO_BADF,

  poll_oneoff: () => ERRNO_NOSYS,
  sched_yield: () => ERRNO_SUCCESS,
  proc_exit: (code) => { throw new ProcExit(code); },
};

// --------------------------------------------------------------------------
// Instantiation. 
// Workers only allows instantiating modules imported as CompiledWasm;
// imports are matched against what the module actually asks for.
// --------------------------------------------------------------------------

function buildImports(module) {
  const imports = {};
  for (const descriptor of WebAssembly.Module.imports(module)) {
    if (descriptor.kind !== "function") {
      continue;
    }
    const { module: moduleName, name } = descriptor;
    const namespace = (imports[moduleName] ??= {});
    if (moduleName === "cf") {
      namespace[name] = cfImports[name]
        ?? (() => { throw new Error(`Unknown cf import '${name}' — shim/library version mismatch?`); });
    } else if (moduleName.startsWith("wasi_snapshot_preview")) {
      namespace[name] = wasiImports[name] ?? (() => ERRNO_NOSYS);
    } else {
      namespace[name] = () => { throw new Error(`Unimplemented wasm import ${moduleName}.${name}`); };
    }
  }
  return imports;
}

function ensureInstance() {
  if (instance !== null) {
    return;
  }
  instance = new WebAssembly.Instance(wasmModule, buildImports(wasmModule));
  try {
    instance.exports._initialize(); // wasi-libc constructors

    // argc includes the executable name. Keep argv in native heap memory for
    // the instance lifetime, just like a process's original command line.
    const programName = textEncoder.encode("worker\0");
    const argvPtr = instance.exports.malloc(8 + programName.length);
    if (argvPtr === 0) {
      throw new Error("Could not allocate Worker command-line arguments.");
    }

    const view = memoryView(); // malloc may have grown the memory

    view.setUint32(argvPtr, argvPtr + 8, true); // argv[0]
    view.setUint32(argvPtr + 4, 0, true);       // argv[1] = NULL

    memoryBytes().set(programName, argvPtr + 8);

    // The reverse P/Invoke entry initializes NativeAOT automatically. Reactor
    // startup lets Main return without running native destructors or exiting.
    const exitCode = instance.exports.__managed__Main(1, argvPtr);
    if (exitCode !== 0) {
      throw new Error(`Managed Worker initialization failed with exit code ${exitCode}.`);
    }
  } catch (error) {
    instance = null;
    throw error;
  } finally {
    flushStd(1, true);
    flushStd(2, true);
  }
}

function errorResponse(error) {
  const detail = error instanceof Error ? `${error.message}\n${error.stack ?? ""}` : String(error);
  return new Response(`Cloudflare.Workers.Hosting error: ${detail}`, {
    status: 500,
    headers: { "content-type": "text/plain; charset=utf-8" },
  });
}

export default {
  async fetch(request, env, ctx) {
    try {
      ensureInstance();
      if (!exportsOf().cf_fetch) {
        throw new Error("The module exports no cf_fetch — is a [CloudflareWorker] class implementing IFetchHandler present?");
      }
      const promiseId = createPromise();
      const { promise } = promises.get(promiseId);
      exportsOf().cf_fetch(holdToHeap(request), holdToHeap(env), holdToHeap(ctx), promiseId);
      const response = await promise;
      flushStd(1, true);
      flushStd(2, true);
      return response;
    } catch (error) {
      console.error("Cloudflare.Workers.Hosting fetch failed:", error);
      return errorResponse(error);
    }
  },

  async scheduled(controller, env, ctx) {
    ensureInstance();
    if (!exportsOf().cf_scheduled) {
      throw new Error("The module exports no cf_scheduled — the [CloudflareWorker] class does not implement IScheduledHandler.");
    }
    const promiseId = createPromise();
    const { promise } = promises.get(promiseId);
    exportsOf().cf_scheduled(holdToHeap(controller), holdToHeap(env), holdToHeap(ctx), promiseId);
    await promise;
    flushStd(1, true);
    flushStd(2, true);
  },
};
