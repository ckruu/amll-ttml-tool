import {
	CheckmarkCircle16Regular,
	Copy24Regular,
	Delete24Regular,
	PaintBrush24Regular,
} from "@fluentui/react-icons";
import {
	Button,
	Checkbox,
	Flex,
	IconButton,
	Text,
	TextField,
	Tooltip,
} from "@radix-ui/themes";
import { useAtom } from "jotai";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import {
	activeThemeSchemeAtom,
	activeThemeSchemeIdAtom,
	type RadixAccentColor,
	RADIX_ACCENT_COLORS,
	themeSchemesAtom,
	createNewScheme,
	LEGACY_HIGHLIGHT_HEX,
	LEGACY_TIMING_START_HEX,
	LEGACY_TIMING_END_HEX,
} from "$/modules/settings/states/theme";
import { SettingsRow } from "./SettingsGroup";
import styles from "./SettingsDialog.module.css";

const swatchStyle = (color: string): React.CSSProperties => {
	// Approximate Radix step-9 for each accent so swatches look right.
	const approx: Record<string, string> = {
		gray: "#8b8d98",
		gold: "#978365",
		bronze: "#a18072",
		brown: "#ad7f58",
		yellow: "#f5d90a",
		amber: "#ffc53d",
		orange: "#f76b15",
		tomato: "#e54d2e",
		red: "#e5484d",
		ruby: "#e54666",
		crimson: "#e93d82",
		pink: "#d6409f",
		plum: "#ab4aba",
		purple: "#8e4ec6",
		violet: "#6e56cf",
		iris: "#5b5bd6",
		indigo: "#3e63dd",
		blue: "#0090ff",
		cyan: "#12a594",
		teal: "#12a594",
		jade: "#29a383",
		green: "#30a46c",
		grass: "#46a758",
		lime: "#bdee63",
		mint: "#86ead4",
		sky: "#01a7c2",
	};
	return { backgroundColor: approx[color] ?? "var(--accent-9)" };
};

function AccentSwatches({
	value,
	onChange,
	label,
}: {
	value: RadixAccentColor;
	onChange: (c: RadixAccentColor) => void;
	label: string;
}) {
	return (
		<div className={styles.paletteButtonRow} role="radiogroup" aria-label={label}>
			{RADIX_ACCENT_COLORS.map((color) => (
				<Tooltip key={color} content={color}>
					<button
						type="button"
						className={styles.paletteButton}
						data-active={value === color || undefined}
						onClick={() => onChange(color)}
						aria-label={color}
						aria-pressed={value === color}
						title={color}
					>
						<span className={styles.palettePreview} style={swatchStyle(color)} />
					</button>
				</Tooltip>
			))}
		</div>
	);
}

const THEME_COLOR_PRESETS = [
	"#30a46c",
	"#12a594",
	"#0090ff",
	"#3e63dd",
	"#8e4ec6",
	"#d6409f",
	"#e5484d",
	"#e54d2e",
	"#f76b15",
	"#ffc53d",
	"#8b8d98",
] as const;

/**
 * Shared preset-swatches + custom-hex field for theme colors stored as hex
 * (active-word highlight, timing chips). Applied with translucency in CSS.
 */
function ThemeColorField({
	value,
	onChange,
	label,
}: {
	value: string;
	onChange: (hex: string) => void;
	label: string;
}) {
	const normalized = value.toLowerCase();
	return (
		<Flex align="center" gap="2" wrap="wrap">
			<div className={styles.paletteButtonRow} role="radiogroup" aria-label={label}>
				{THEME_COLOR_PRESETS.map((color) => (
					<Tooltip key={color} content={color}>
						<button
							type="button"
							className={styles.paletteButton}
							data-active={normalized === color || undefined}
							onClick={() => onChange(color)}
							aria-label={color}
							aria-pressed={normalized === color}
							title={color}
						>
							<span
								className={styles.palettePreview}
								style={{ backgroundColor: color }}
							/>
						</button>
					</Tooltip>
				))}
			</div>
			<input
				type="color"
				value={
					/^#[0-9a-fA-F]{6}$/.test(value) ? value : THEME_COLOR_PRESETS[0]
				}
				onChange={(e) => onChange(e.target.value)}
				style={{
					border: "none",
					padding: 0,
					background: "none",
					width: "28px",
					height: "28px",
				}}
				aria-label={label}
			/>
			<TextField.Root
				size="1"
				placeholder="#30a46c"
				value={value}
				onChange={(e) => onChange(e.target.value)}
				style={{ maxWidth: "110px" }}
			/>
		</Flex>
	);
}

export const SettingsThemePage = () => {
	const { t } = useTranslation();
	const [schemes, setSchemes] = useAtom(themeSchemesAtom);
	const [activeId, setActiveId] = useAtom(activeThemeSchemeIdAtom);
	const [activeScheme] = useAtom(activeThemeSchemeAtom);
	const [newName, setNewName] = useState("");
	const [renamingId, setRenamingId] = useState<string | null>(null);
	const [renameDraft, setRenameDraft] = useState("");

	const updateActive = (patch: Partial<typeof activeScheme>) => {
		setSchemes(schemes.map((s) => (s.id === activeId ? { ...s, ...patch } : s)));
	};

	const handleDuplicate = (id: string) => {
		const src = schemes.find((s) => s.id === id);
		if (!src) return;
		const copy = {
			...src,
			id: `${Date.now()}-${Math.random().toString(36).slice(2)}`,
			name: t("settings.theme.copyName", "{name} copy", { name: src.name }),
		};
		setSchemes([...schemes, copy]);
		setActiveId(copy.id);
	};

	const handleDelete = (id: string) => {
		if (schemes.length <= 1) return;
		const next = schemes.filter((s) => s.id !== id);
		setSchemes(next);
		if (id === activeId) setActiveId(next[0].id);
	};

	const handleCreate = () => {
		const scheme = createNewScheme(
			newName.trim() ||
				t("settings.theme.untitledScheme", "Untitled scheme"),
		);
		setSchemes([...schemes, scheme]);
		setActiveId(scheme.id);
		setNewName("");
	};

	const commitRename = () => {
		if (!renamingId) return;
		const name = renameDraft.trim();
		if (name) {
			setSchemes(
				schemes.map((s) => (s.id === renamingId ? { ...s, name } : s)),
			);
		}
		setRenamingId(null);
	};

	return (
		<Flex direction="column" gap="3">
			<SettingsRow
				icon={<PaintBrush24Regular />}
				title={t("settings.theme.accent", "Interface accent color")}
				description={t(
					"settings.theme.accentDesc",
					"Pick a Radix preset for light and dark mode, or enter an exact hex color.",
				)}
			/>
			<Flex direction="column" gap="3">
				<Flex direction="column" gap="2">
					<Text size="2" weight="bold">
						{t("settings.theme.lightAccent", "Light mode accent")}
						{activeScheme.useCustomLight && activeScheme.customLight
							? ` — ${activeScheme.customLight}`
							: ` — ${activeScheme.light}`}
					</Text>
					<AccentSwatches
						value={activeScheme.light}
						onChange={(c) => updateActive({ light: c })}
						label="light accent"
					/>
					<Flex align="center" gap="2">
						<Text as="label" size="2">
							<Flex gap="2" align="center">
								<Checkbox
									checked={activeScheme.useCustomLight}
									onCheckedChange={(c) =>
										updateActive({ useCustomLight: Boolean(c) })
									}
								/>
								{t("settings.theme.useCustom", "Use custom hex")}
							</Flex>
						</Text>
						<input
							type="color"
							value={
								/^#[0-9a-fA-F]{6}$/.test(activeScheme.customLight ?? "")
									? (activeScheme.customLight as string)
									: "#30a46c"
							}
							onChange={(e) =>
								updateActive({
									customLight: e.target.value,
									useCustomLight: true,
								})
							}
							style={{
								border: "none",
								padding: 0,
								background: "none",
								width: "28px",
								height: "28px",
							}}
							aria-label="custom light color"
						/>
						<TextField.Root
							size="1"
							placeholder="#30a46c"
							value={activeScheme.customLight ?? ""}
							onChange={(e) =>
								updateActive({ customLight: e.target.value })
							}
							style={{ maxWidth: "110px" }}
						/>
					</Flex>
				</Flex>

				<Flex direction="column" gap="2">
					<Text size="2" weight="bold">
						{t("settings.theme.darkAccent", "Dark mode accent")}
						{activeScheme.useCustomDark && activeScheme.customDark
							? ` — ${activeScheme.customDark}`
							: ` — ${activeScheme.dark}`}
					</Text>
					<AccentSwatches
						value={activeScheme.dark}
						onChange={(c) => updateActive({ dark: c })}
						label="dark accent"
					/>
					<Flex align="center" gap="2">
						<Text as="label" size="2">
							<Flex gap="2" align="center">
								<Checkbox
									checked={activeScheme.useCustomDark}
									onCheckedChange={(c) =>
										updateActive({ useCustomDark: Boolean(c) })
									}
								/>
								{t("settings.theme.useCustom", "Use custom hex")}
							</Flex>
						</Text>
						<input
							type="color"
							value={
								/^#[0-9a-fA-F]{6}$/.test(activeScheme.customDark ?? "")
									? (activeScheme.customDark as string)
									: "#29a383"
							}
							onChange={(e) =>
								updateActive({
									customDark: e.target.value,
									useCustomDark: true,
								})
							}
							style={{
								border: "none",
								padding: 0,
								background: "none",
								width: "28px",
								height: "28px",
							}}
							aria-label="custom dark color"
						/>
						<TextField.Root
							size="1"
							placeholder="#29a383"
							value={activeScheme.customDark ?? ""}
							onChange={(e) => updateActive({ customDark: e.target.value })}
							style={{ maxWidth: "110px" }}
						/>
					</Flex>
				</Flex>
			</Flex>

			<Flex direction="column" gap="2">
				<Text size="2" weight="bold">
					{t("settings.theme.highlight", "Active word highlight")}
				</Text>
				<Text size="1" color="gray">
					{t(
						"settings.theme.highlightDesc",
						"Color of the currently playing word in the sync view. Stored per light/dark appearance.",
					)}
				</Text>
				<Text size="2">
					{t("settings.theme.highlightLight", "Light appearance")} —{" "}
					{activeScheme.highlightLight ?? LEGACY_HIGHLIGHT_HEX}
				</Text>
				<ThemeColorField
					value={activeScheme.highlightLight ?? LEGACY_HIGHLIGHT_HEX}
					onChange={(hex) => updateActive({ highlightLight: hex })}
					label="highlight light"
				/>
				<Text size="2">
					{t("settings.theme.highlightDark", "Dark appearance")} —{" "}
					{activeScheme.highlightDark ?? LEGACY_HIGHLIGHT_HEX}
				</Text>
				<ThemeColorField
					value={activeScheme.highlightDark ?? LEGACY_HIGHLIGHT_HEX}
					onChange={(hex) => updateActive({ highlightDark: hex })}
					label="highlight dark"
				/>
			</Flex>

			<Flex direction="column" gap="2">
				<Text size="2" weight="bold">
					{t("settings.theme.timing", "Timing chips")}
				</Text>
				<Text size="1" color="gray">
					{t(
						"settings.theme.timingDesc",
						"Start/end timestamp colors in the time view. Kept distinct so start and end are easy to tell apart.",
					)}
				</Text>
				<Text size="2">
					{t("settings.theme.timingStart", "Start time")} —{" "}
					{activeScheme.timingStart ?? LEGACY_TIMING_START_HEX}
				</Text>
				<ThemeColorField
					value={activeScheme.timingStart ?? LEGACY_TIMING_START_HEX}
					onChange={(hex) => updateActive({ timingStart: hex })}
					label="timing start"
				/>
				<Text size="2">
					{t("settings.theme.timingEnd", "End time")} —{" "}
					{activeScheme.timingEnd ?? LEGACY_TIMING_END_HEX}
				</Text>
				<ThemeColorField
					value={activeScheme.timingEnd ?? LEGACY_TIMING_END_HEX}
					onChange={(hex) => updateActive({ timingEnd: hex })}
					label="timing end"
				/>
			</Flex>

			<Flex direction="column" gap="2">
				<Text size="2" weight="bold">
					{t("settings.theme.schemes", "Color schemes")}
				</Text>
				{schemes.map((scheme) => {
					const isActive = scheme.id === activeId;
					const isRenaming = renamingId === scheme.id;
					return (
						<Flex
							key={scheme.id}
							align="center"
							gap="2"
							p="2"
							style={{
								border: isActive
									? "1px solid var(--accent-9)"
									: "1px solid var(--gray-a5)",
								borderRadius: "var(--radius-3)",
							}}
						>
							<span
								style={{
									display: "inline-flex",
									gap: 2,
								}}
							>
								<span
									style={{
										width: 16,
										height: 16,
										borderRadius: 4,
										display: "inline-block",
										...swatchStyle(scheme.light),
									}}
								/>
								<span
									style={{
										width: 16,
										height: 16,
										borderRadius: 4,
										display: "inline-block",
										...swatchStyle(scheme.dark),
									}}
								/>
							</span>
							{isRenaming ? (
								<TextField.Root
									size="1"
									value={renameDraft}
									onChange={(e) => setRenameDraft(e.target.value)}
									onBlur={commitRename}
									onKeyDown={(e) => {
										if (e.key === "Enter") commitRename();
										if (e.key === "Escape") setRenamingId(null);
									}}
									style={{ flex: 1 }}
								/>
							) : (
								<Button
									variant="ghost"
									color="gray"
									onClick={() => {
										setRenamingId(scheme.id);
										setRenameDraft(scheme.name);
									}}
									style={{ flex: 1, justifyContent: "flex-start" }}
								>
									<Text size="2" weight={isActive ? "bold" : undefined}>
										{scheme.name}
									</Text>
								</Button>
							)}
							{isActive && (
								<Text size="1" color="gray">
									<CheckmarkCircle16Regular />
								</Text>
							)}
							{!isActive && (
								<Button
									size="1"
									variant="soft"
									onClick={() => setActiveId(scheme.id)}
								>
									{t("common.apply", "Apply")}
								</Button>
							)}
							<Tooltip content={t("settings.theme.duplicate", "Duplicate")}>
								<IconButton
									size="1"
									variant="ghost"
									color="gray"
									onClick={() => handleDuplicate(scheme.id)}
									aria-label="duplicate scheme"
								>
									<Copy24Regular />
								</IconButton>
							</Tooltip>
							<Tooltip content={t("common.remove", "Remove")}>
								<IconButton
									size="1"
									variant="ghost"
									color="red"
									disabled={schemes.length <= 1}
									onClick={() => handleDelete(scheme.id)}
									aria-label="delete scheme"
								>
									<Delete24Regular />
								</IconButton>
							</Tooltip>
						</Flex>
					);
				})}
				<Flex gap="2" align="center">
					<TextField.Root
						size="1"
						placeholder={t(
							"settings.theme.newSchemePlaceholder",
							"New scheme name…",
						)}
						value={newName}
						onChange={(e) => setNewName(e.target.value)}
						onKeyDown={(e) => {
							if (e.key === "Enter") handleCreate();
						}}
						style={{ flex: 1 }}
					/>
					<Button size="1" variant="soft" onClick={handleCreate}>
						{t("settings.theme.newScheme", "New scheme")}
					</Button>
				</Flex>
				<Text size="1" color="gray">
					{t(
						"settings.theme.schemesHint",
						"Click a scheme name to rename. Applying a scheme changes the accent immediately and is saved automatically.",
					)}
				</Text>
			</Flex>
		</Flex>
	);
};
