// Whisper, in a worker so the page keeps drawing while it thinks. transformers.js is vendored under lib/ because the
// CSP runs scripts from this origin only; what it fetches is data — the ONNX runtime's .wasm from jsdelivr (pinned
// to the exact build the bundle was made against) and the model from Hugging Face — and both stay in the browser's
// cache after the first time.
import { pipeline, env } from "../lib/transformers/transformers.min.js";

const MODEL = "Xenova/whisper-tiny.en";
const ORT = "https://cdn.jsdelivr.net/npm/onnxruntime-web@1.31.0-dev.20260914-8d85527a0/dist/";

env.allowLocalModels = false;

// Only the binary, never the loader: the bundle carries its own, and handing it one makes transformers.js import it
// from a blob: URL, which script-src does not allow.
env.useWasmCache = false;
env.backends.onnx.wasm.wasmPaths = { wasm: ORT + "ort-wasm-simd-threaded.asyncify.wasm" };

let loading = null;

async function load() {
  // Asked once, up front. navigator.gpu can exist with no adapter behind it (no driver, GPU blocklisted), and a
  // WebGPU attempt that fails leaves the runtime unable to start WASM in the same worker — measured, not guessed.
  const adapter = typeof navigator !== "undefined" && navigator.gpu ? await navigator.gpu.requestAdapter().catch(() => null) : null;
  const device = adapter ? "webgpu" : "wasm";

  // q8 for both backends: one 41 MB download serves either.
  const transcriber = await pipeline("automatic-speech-recognition", MODEL, { device: device, dtype: "q8" });
  return { transcriber: transcriber, device: device };
}

self.onmessage = async function (e) {
  const msg = e.data || {};
  if (msg.type === "warm") {
    loading ??= load();
    try {
      const ready = await loading;
      self.postMessage({ type: "ready", device: ready.device });
    } catch (err) {
      loading = null;
      self.postMessage({ type: "failed", error: String(err && err.message || err) });
    }
    return;
  }

  if (msg.type === "run") {
    try {
      const ready = await loading;
      const out = await ready.transcriber(msg.samples, { chunk_length_s: 30, stride_length_s: 5 });
      self.postMessage({ type: "text", id: msg.id, text: out && out.text || "" });
    } catch (err) {
      self.postMessage({ type: "text", id: msg.id, text: null, error: String(err && err.message || err) });
    }
  }
};
