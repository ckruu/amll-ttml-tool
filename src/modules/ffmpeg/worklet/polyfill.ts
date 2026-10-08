if (typeof globalThis.TextDecoder === "undefined") {
	(globalThis as any).TextDecoder = class TextDecoder {
		decode(arr?: Uint8Array) {
			if (!arr) return "";
			return String.fromCharCode.apply(null, arr as any);
		}
	};
}
