import { atomWithKeybindingStorage } from "$/utils/keybindings";
import { Logger } from "$/utils/logger";
import type { I18nKey, KeyBindingCommand, KeyBindingsConfig } from "./types";

const commandsMap = new Map<string, KeyBindingCommand>();

export function registerCommand(
	id: string,
	defaultKeys: KeyBindingsConfig,
	description: I18nKey,
	category = "General",
) {
	const commandAtom = atomWithKeybindingStorage(id, defaultKeys);

	const command: KeyBindingCommand = {
		id,
		defaultKeys,
		description,
		category,
		atom: commandAtom,
	};

	if (commandsMap.has(id)) {
		Logger.warn("KeyBindingRegistry", `Duplicate command registered: ${id}`);
	}
	commandsMap.set(id, command);

	return command;
}

export function getAllCommands(): KeyBindingCommand[] {
	return Array.from(commandsMap.values());
}

export function getCommandById(id: string): KeyBindingCommand | undefined {
	return commandsMap.get(id);
}
