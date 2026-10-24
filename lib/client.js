/**
 * dsh-screenshot-native — browser half.
 *
 * Contributes one camera control to the composer tool row. Pressing it asks the host for
 * `POST /dsh-screenshot-native/native`, which runs a native WinForms overlay covering the whole
 * virtual desktop (so it can grab windows OUTSIDE the Harness window), minimizes the Harness
 * window while it is up, and answers with one PNG. This module then hands that PNG to the
 * composer as if the user pasted it.
 *
 * Every Windows-native operation lives in the host half and in `bin/WeChatShot.exe`; this module
 * only renders the button and calls the plugin's HTTP route.
 *
 * Compiled client-module form: `window.__ModuleLoader__.load({ id, factory })`.
 *
 * `id` and `exports.name` MUST equal the package name. The client-module registry keys
 * factories by this id and refuses a duplicate, so a stale id after a rename fails the
 * whole entry with "duplicate factory registration".
 */
window.__ModuleLoader__.load({
	id: "dsh-screenshot-native",
	factory: (require) => {
		var module = { exports: {} };
		var exports = module.exports;
		Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });

		let react = require("react");

		const PREFIX = "/dsh-screenshot-native";
		const STYLE_ID = "dsh-screenshot-styles";

		/**
		 * Screen pixels kept uncovered at the very top of the window while snipping.
		 *
		 * The Harness chrome (window frame plus the 应用/编辑 menu row) occupies roughly
		 * the first 62px of the window. Inside this strip the overlay is neither drawn
		 * nor interactive, so the menu row stays readable and clickable while the user
		 * is choosing a capture area.
		 */
		const TOP_SAFE = 48;

		const CSS = `
.dshss-root { position: fixed; inset: 0; z-index: 2147483000;
  font: 12px/1.4 system-ui, -apple-system, "Segoe UI", sans-serif;
  pointer-events: none; }
/* The interaction layer sets its own cursor (crosshair / resize / move) and re-enables
   pointer events, so the root must not force one. */
.dshss-layer { cursor: crosshair; }
/* The dimmer, the frozen shot and the selection all start below the top safe strip;
   interactive children re-enable pointer events for themselves. */
.dshss-dim { position: absolute; left: 0; right: 0; bottom: 0; background: rgba(0,0,0,.35);
  pointer-events: none; }
.dshss-shot { position: absolute; left: 0; transform-origin: 0 0; image-rendering: crisp-edges;
  -webkit-user-drag: none; user-select: none; pointer-events: none; }
.dshss-sel { position: absolute; box-shadow: 0 0 0 9999px rgba(0,0,0,.5);
  outline: 1px solid rgba(255,255,255,.95); pointer-events: none; }
/* Resize handles. Positioned with a translate so the marker is centred exactly on the
   selection corner; hit-testing lives in JS (handleAt) and uses a larger radius than
   the 11px drawn size. */
.dshss-handle { position: absolute; width: 11px; height: 11px;
  transform: translate(-50%, -50%); background: #fff;
  border: 1px solid rgba(0,0,0,.65); border-radius: 2px;
  box-shadow: 0 1px 3px rgba(0,0,0,.45); pointer-events: none; }
.dshss-snap { position: absolute; outline: 2px solid rgba(90,190,255,.95);
  pointer-events: none; background: rgba(90,190,255,.14);
  box-shadow: 0 0 0 1px rgba(0,0,0,.35), inset 0 0 24px rgba(90,190,255,.2); }
.dshss-bar { position: absolute; display: flex; align-items: center; gap: 6px;
  padding: 6px 8px; border-radius: 10px; background: rgba(28,28,32,.95);
  color: #eee; box-shadow: 0 6px 24px rgba(0,0,0,.5); pointer-events: auto;
  white-space: nowrap; z-index: 2147483005; box-sizing: border-box; }
.dshss-btn { box-sizing: border-box; display: inline-flex; align-items: center;
  justify-content: center; min-height: 28px; padding: 4px 10px;
  border: 1px solid rgba(255,255,255,.16); border-radius: 6px; background: rgba(255,255,255,.06);
  color: #eee; font: inherit; cursor: pointer; }
.dshss-btn:hover { background: rgba(255,255,255,.14); }
.dshss-btn[data-primary="1"] { background: #2f6feb; border-color: #2f6feb; color: #fff; }
.dshss-btn[data-primary="1"]:hover { background: #3b7cf7; }
.dshss-btn[data-on="1"] { background: rgba(47,111,235,.3); border-color: #2f6feb; }
.dshss-btn:disabled { opacity: .45; cursor: default; }
.dshss-size { position: absolute; padding: 2px 6px; border-radius: 4px;
  background: rgba(28,28,32,.92); color: #eee; font: 11px/1.4 ui-monospace, Consolas, monospace; }
.dshss-hint { position: fixed; left: 50%; bottom: 16px; transform: translateX(-50%);
  padding: 6px 14px; border-radius: 999px; background: rgba(28,28,32,.94); color: #ddd;
  z-index: 2147483002; pointer-events: none; max-width: 80vw; overflow: hidden;
  text-overflow: ellipsis; white-space: nowrap; }
.dshss-err { position: fixed; left: 50%; bottom: 60px; transform: translateX(-50%);
  padding: 6px 14px; border-radius: 8px; background: rgba(120,30,30,.96); color: #ffdede;
  z-index: 2147483002; max-width: 70vw; }
.dshss-tool { box-sizing: border-box; display: inline-flex; align-items: center;
  justify-content: center; width: 30px; height: 30px; border: 0; border-radius: 8px;
  background: transparent; color: currentColor; cursor: pointer; opacity: .78;
  padding: 0; margin: 0; position: relative; line-height: 0; }
.dshss-tool > svg { display: block; pointer-events: none; }
.dshss-tool:hover { opacity: 1; background: rgba(127,127,127,.16); }
.dshss-tool[data-busy="1"] { opacity: .4; cursor: default; }
.dshss-tool[data-pref="keep"]::after { content: ""; position: absolute; right: 2px; bottom: 2px;
  width: 5px; height: 5px; border-radius: 50%; background: #f0a020; }
/* Capture icon and its preference caret read as one joined control. */
.dshss-group { position: relative; display: inline-flex; align-items: center; gap: 0; }
.dshss-caret { box-sizing: border-box; display: inline-flex; align-items: center;
  justify-content: center; width: 17px; height: 30px; margin-left: -4px; padding: 0;
  border: 0; border-radius: 8px; background: transparent; color: currentColor;
  cursor: pointer; opacity: .62; line-height: 0; }
.dshss-caret > svg { display: block; pointer-events: none; }
.dshss-caret:hover { opacity: 1; background: rgba(127,127,127,.16); }
.dshss-caret[data-open="1"] { opacity: 1; background: rgba(127,127,127,.2); }
.dshss-caret:disabled { opacity: .3; cursor: default; }
/* Dropdown card, built to match @deepseek-ai/dsh-client-ui-primitives Menu.module.css.
   Sizing follows the PRIMARY card scale (item: 13px text, 34px min-height, 6px/8px
   padding, radius-md rows, radius-lg card) — not the compactList variant, which is
   noticeably tighter than the menus the app actually shows in the composer.
   Theme tokens carry the light-theme literal as fallback so the card stays correct
   even when rendered outside the token scope. */
.dshss-menu { position: fixed; z-index: 2147483006;
  box-sizing: border-box; display: flex; flex-direction: column; gap: 0;
  min-width: 180px; max-width: 360px; padding: 4px; border: 0;
  border-radius: var(--dsw-radius-lg, 16px);
  background: var(--dsw-alias-bg-layer-1, #fff);
  color: var(--dsw-alias-label-primary, #0f1115);
  box-shadow: var(--dsw-elevation-prominent, 0 0 0 1px #00000014, 0 3px 8px 0 #0000000a, 0 0 20px 0 #0000000d);
  font: 13px/20px system-ui, -apple-system, "Segoe UI", sans-serif; }
.dshss-menu-title { padding: 6px 8px; font-size: 11px; line-height: 15px;
  color: var(--dsw-alias-label-tertiary, #61666b); }
.dshss-menu-item { box-sizing: border-box; display: flex; align-items: center; gap: 6px;
  width: 100%; min-height: 34px; padding: 6px 8px;
  /* appearance:none is load-bearing: a <button> keeps its native chrome otherwise,
     which renders as a grey rounded box exactly like an unstyled control. */
  appearance: none; -webkit-appearance: none;
  border: 0; border-radius: var(--dsw-radius-md, 12px); background: transparent;
  cursor: pointer; font: inherit; font-size: 13px; line-height: 20px;
  color: var(--dsw-alias-label-primary, #0f1115); text-align: left; }
.dshss-menu-item:hover { background: var(--dsw-alias-interactive-bg-hover, #2631480f); }
.dshss-menu-item[data-on="1"] { background: var(--dsw-alias-interactive-bg-hover, #2631480f); }
/* Trailing marker: the label takes the free space, the hint and the check sit at
   the end of the row (mirrors the app's own select-style menus). */
.dshss-menu-mark { display: inline-flex; flex: none; width: 14px; height: 14px;
  margin-inline-start: auto; align-items: center; justify-content: center;
  color: var(--dsw-alias-menu-icon, #353638); }
.dshss-menu-hint { flex: none; margin-inline-start: 10px;
  color: var(--dsw-alias-label-tertiary, #61666b); font-size: 11px; line-height: 16px; }
.dshss-menu-text { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis;
  white-space: nowrap; }
`;

		function ensureStyles() {
			if (document.getElementById(STYLE_ID) !== null) return;
			const el = document.createElement("style");
			el.id = STYLE_ID;
			el.textContent = CSS;
			document.head.appendChild(el);
		}

		async function api(path, options) {
			const res = await fetch(PREFIX + path, Object.assign(
				{ headers: { "Content-Type": "application/json" } }, options));
			const text = await res.text();
			let data = null;
			try { data = text === "" ? null : JSON.parse(text); } catch (e) { data = null; }
			if (!res.ok) throw new Error((data && data.error) || ("HTTP " + String(res.status)));
			return data;
		}

		function IconFrame() {
			return react.createElement("svg", {
				width: 16, height: 16, viewBox: "0 0 16 16", fill: "none",
				stroke: "currentColor", strokeWidth: 1.4, strokeLinecap: "round",
				strokeLinejoin: "round", "aria-hidden": "true",
			},
				react.createElement("path", { d: "M2 5.2V3.4A1.4 1.4 0 0 1 3.4 2h1.8" }),
				react.createElement("path", { d: "M10.8 2h1.8A1.4 1.4 0 0 1 14 3.4v1.8" }),
				react.createElement("path", { d: "M14 10.8v1.8a1.4 1.4 0 0 1-1.4 1.4h-1.8" }),
				react.createElement("path", { d: "M5.2 14H3.4A1.4 1.4 0 0 1 2 12.6v-1.8" }),
				react.createElement("circle", { cx: 8, cy: 8, r: 2.1 })
			);
		}

		/**
		 * Hand a captured PNG to the composer as if the user pasted it.
		 *
		 * The composer is a Lexical editor whose own paste handler reads
		 * `event.clipboardData.items` and calls `getAsFile()` on every file item
		 * (dsh-client-ui-conversation, `editor.registerCommand(<paste>, ...)`), so a
		 * synthetic ClipboardEvent reaches exactly the code path a real Ctrl+V uses.
		 * No DSH internal API is touched.
		 *
		 * @returns {Promise<boolean>} whether a file item was handed to the editor.
		 */
		async function insertIntoComposer(dataUrl) {
			// A session may render more than one editor; take the visible one, which
			// is the composer the user is actually typing into.
			const editors = document.querySelectorAll('[data-lexical-editor="true"]');
			let editor = null;
			for (const candidate of editors) {
				const box = candidate.getBoundingClientRect();
				if (box.width > 0 && box.height > 0) editor = candidate;
			}
			if (editor === null) editor = editors[0] ?? null;
			if (editor === null) return false;

			const comma = dataUrl.indexOf(",");
			const binary = window.atob(comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl);
			const bytes = new Uint8Array(binary.length);
			for (let i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i);

			const file = new window.File([bytes], "screenshot-" + String(Date.now()) + ".png", { type: "image/png" });
			const transfer = new window.DataTransfer();
			transfer.items.add(file);

			// focus() first: the paste command runs against the focused editor.
			editor.focus();

			// Both keys are supplied on purpose. ClipboardEventInit is specified as
			// `dataTransfer`, while the read-only event property every consumer uses is
			// `clipboardData`; setting both keeps the event valid whichever the
			// implementation honours.
			let event = null;
			try {
				event = new window.ClipboardEvent("paste", {
					dataTransfer: transfer,
					clipboardData: transfer,
					bubbles: true,
					cancelable: true,
					composed: true,
				});
			} catch (e) {
				event = new window.ClipboardEvent("paste", { bubbles: true, cancelable: true, composed: true });
			}
			// Some engines ignore constructor init for clipboardData: define it directly.
			if (event.clipboardData === null || event.clipboardData === undefined) {
				try {
					Object.defineProperty(event, "clipboardData", { value: transfer, configurable: true });
				} catch (e) { /* leave as-is and let the caller report failure */ }
			}
			if (event.clipboardData === null || event.clipboardData === undefined) return false;

			// A native event would carry isTrusted=true, which cannot be forged; the
			// Lexical handler does not check it, so a synthetic dispatch works.
			editor.dispatchEvent(event);
			return true;
		}


		/** Composer tool-row camera button: one click, native overlay, picture into the input. */
		function CaptureButton() {
			const [busy, setBusy] = react.useState(false);
			const [error, setError] = react.useState(null);

			const start = react.useCallback(async () => {
				setBusy(true);
				setError(null);
				try {
					const native = await api("/native", { method: "POST", body: "{}" });
					if (native === null || native.ok !== true) {
						throw new Error(String((native && native.message) || "截图失败"));
					}
					// busy = another overlay is already open; cancelled = the user pressed Esc / right-click.
					if (native.cancelled === true || native.busy === true) return;
					if (typeof native.dataUrl !== "string" || native.dataUrl === "") {
						throw new Error("截图没有返回图片数据");
					}
					const inserted = await insertIntoComposer(native.dataUrl);
					if (!inserted) setError("无法把截图插入输入框");
				} catch (e) {
					setError(String(e && e.message ? e.message : e));
				} finally {
					setBusy(false);
				}
			}, []);

			return react.createElement(react.Fragment, null,
				react.createElement("span", { className: "dshss-group" },
					react.createElement("button", {
						className: "dshss-tool",
						"data-busy": busy ? "1" : "0",
						disabled: busy,
						title: error !== null ? ("截图失败：" + error) : "截图（框选后直接进输入框）",
						onClick: () => { start(); },
					}, react.createElement(IconFrame, null)),
				),
				error !== null ? react.createElement("div", { className: "dshss-err" }, error) : null,
			);
		}

		/**
		 * The services this browser half needs.
		 *
		 * These are CLIENT service names, not npm package names: every working
		 * client bundle on this machine declares its own, e.g.
		 *   dsh-rewind-plugin : ["slots", "sessions", "locale", "commandUi", "configForms"]
		 *   dsh-context       : ["slots", "locale"]
		 *   dsh-mnemon        : ["slots", "sessions", "workspaces", "uiSession", "connection", "locale", "layout"]
		 * A client bundle that omits `inject` never reports the service it needs,
		 * so the web boot audit records the entry as not activated.
		 */
		const inject = ["slots"];

		/** Cordis plugin body for the browser half. */
		function apply(ctx) {
			ensureStyles();

			// `conversation.input.left` is a list slot: id, order and label form the
			// registration contract; the component is the second argument.
			const dispose = ctx.slots.inject("conversation.input.left", () => ctx.slots.register({
				name: "conversation.input.left",
				id: "dsh-screenshot",
				order: 42,
				label: () => "截图",
			}, CaptureButton));

			ctx.effect(() => () => {
				if (typeof dispose === "function") dispose();
				const el = document.getElementById(STYLE_ID);
				if (el !== null) el.remove();
			});
		}

		exports.name = "dsh-screenshot-native";
		exports.inject = inject;
		exports.apply = apply;
		return exports;
	},
});
