import { useAtomValue } from "jotai";
import type { DependencyList } from "react";
import { type KeyBindingCallback, useKeyBinding } from "$/utils/keybindings";
import type { KeyBindingCommand } from "./types";
export function useCommand(
	command: KeyBindingCommand,
	callback: KeyBindingCallback,
	deps: DependencyList = [],
) {
	const currentKeys = useAtomValue(command.atom);

	useKeyBinding(currentKeys, callback, deps);
}
