type CxArg = string | false | null | undefined | Record<string, boolean>;
export function cx(...args: CxArg[]): string {
	const out: string[] = [];
	for (const arg of args) {
		if (!arg) continue;
		if (typeof arg === "string") {
			if (arg) out.push(arg);
		} else {
			for (const key of Object.keys(arg)) {
				if (arg[key]) out.push(key);
			}
		}
	}
	return out.join(" ");
}
