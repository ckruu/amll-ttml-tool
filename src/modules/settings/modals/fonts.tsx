import {
	Delete24Regular,
	DocumentAdd24Regular,
	FontIncrease24Regular,
} from "@fluentui/react-icons";
import {
	Button,
	Flex,
	IconButton,
	SegmentedControl,
	Text,
	TextField,
	Tooltip,
} from "@radix-ui/themes";
import { useAtom, useAtomValue, useSetAtom, type WritableAtom } from "jotai";
import { useId, useRef } from "react";
import { useTranslation } from "react-i18next";
import {
	CJK_FONT_PRESETS,
	customFontEntriesAtom,
	fontEditCjkAtom,
	fontEditLatinAtom,
	fontModeAtom,
	fontPreviewCjkAtom,
	fontPreviewLatinAtom,
	fontSimpleCjkAtom,
	fontSimpleLatinAtom,
	fontStacksAtom,
	fontSyncCjkAtom,
	fontSyncLatinAtom,
	fontUiCjkAtom,
	fontUiLatinAtom,
	LATIN_FONT_PRESETS,
	loadedCustomFamiliesAtom,
	registerCustomFontAtom,
	unregisterCustomFontAtom,
	type FontMode,
} from "$/modules/settings/states/fonts";
import { SettingsGroup, SettingsRow } from "./SettingsGroup";

function FontSlotPicker({
	label,
	description,
	valueAtom,
	presets,
}: {
	label: string;
	description?: string;
	valueAtom: WritableAtom<string, [string], void>;
	presets: readonly string[];
}) {
	const [value, setValue] = useAtom(valueAtom);
	const customFamilies = useAtomValue(loadedCustomFamiliesAtom);
	const listId = useId();
	const options = [...presets];
	for (const f of customFamilies) {
		if (!options.includes(f)) options.push(f);
	}
	if (value && !options.includes(value)) options.push(value);

	return (
		<SettingsRow
			icon={<FontIncrease24Regular />}
			title={label}
			description={description}
			action={
				<>
					<TextField.Root
						size="1"
						list={listId}
						value={value}
						onChange={(e) => setValue(e.target.value)}
						placeholder={presets[0]}
						style={{ width: "200px" }}
						aria-label={label}
					/>
					<datalist id={listId}>
						{options.map((name) => (
							<option key={name} value={name} />
						))}
					</datalist>
				</>
			}
		/>
	);
}

function CustomFontFiles() {
	const { t } = useTranslation();
	const [entries] = useAtom(customFontEntriesAtom);
	const registerFont = useSetAtom(registerCustomFontAtom);
	const unregisterFont = useSetAtom(unregisterCustomFontAtom);
	const fileRef = useRef<HTMLInputElement>(null);

	return (
		<Flex direction="column" gap="2">
			<Flex align="center" gap="2">
				<Text size="2" weight="bold" style={{ flex: 1 }}>
					{t("settings.fonts.customFiles", "Installed font files")}
				</Text>
				<Button
					size="1"
					variant="soft"
					onClick={() => fileRef.current?.click()}
				>
					<DocumentAdd24Regular />
					{t("settings.fonts.addFile", "Add font file…")}
				</Button>
				<input
					ref={fileRef}
					type="file"
					hidden
					multiple
					accept=".ttf,.otf,.woff,.woff2,.ttc"
					onChange={async (e) => {
						const files = [...(e.target.files ?? [])];
						for (const file of files) {
							await registerFont(file, file.name);
						}
						e.target.value = "";
					}}
				/>
			</Flex>
			{entries.length === 0 ? (
				<Text size="1" color="gray">
					{t(
						"settings.fonts.customFilesEmpty",
						"No custom fonts installed. Supported: .ttf .otf .woff .woff2 .ttc. Files are stored locally.",
					)}
				</Text>
			) : (
				entries.map((entry) => (
					<Flex key={entry.id} align="center" gap="2">
						<Text size="2" style={{ fontFamily: `"${entry.family}"` }}>
							Aa 漢字
						</Text>
						<Flex direction="column" style={{ flex: 1, minWidth: 0 }}>
							<Text size="2" truncate>
								{entry.family}
							</Text>
							<Text size="1" color="gray" truncate>
								{entry.fileName}
							</Text>
						</Flex>
						<Tooltip content={t("common.remove", "Remove")}>
							<IconButton
								size="1"
								variant="ghost"
								color="red"
								onClick={() => unregisterFont(entry.id)}
								aria-label={`remove ${entry.family}`}
							>
								<Delete24Regular />
							</IconButton>
						</Tooltip>
					</Flex>
				))
			)}
		</Flex>
	);
}

export const SettingsFontsPage = () => {
	const { t } = useTranslation();
	const [fontMode, setFontMode] = useAtom(fontModeAtom);
	const stacks = useAtomValue(fontStacksAtom);

	return (
		<Flex direction="column" gap="3">
			<SettingsGroup title={t("settings.fonts.uiTitle", "Interface font")}>
				<FontSlotPicker
					label={t("settings.fonts.latin", "Latin font")}
					description={t(
						"settings.fonts.uiDesc",
						"Applies to the title bar, ribbon, sidebar, dialogs and other interface chrome. Lyric views keep their own fonts below.",
					)}
					valueAtom={fontUiLatinAtom}
					presets={LATIN_FONT_PRESETS}
				/>
				<FontSlotPicker
					label={t("settings.fonts.cjk", "CJK font")}
					valueAtom={fontUiCjkAtom}
					presets={CJK_FONT_PRESETS}
				/>
			</SettingsGroup>
			<SettingsRow
				icon={<FontIncrease24Regular />}
				title={t("settings.fonts.mode", "Font mode")}
				description={t(
					"settings.fonts.modeDesc",
					"Simple mode uses one Latin + one CJK font everywhere. Advanced mode sets them per view (edit / sync / preview).",
				)}
				action={
					<SegmentedControl.Root
						value={fontMode}
						onValueChange={(v) => setFontMode(v as FontMode)}
					>
						<SegmentedControl.Item value="simple">
							{t("settings.fonts.simple", "Simple")}
						</SegmentedControl.Item>
						<SegmentedControl.Item value="advanced">
							{t("settings.fonts.advanced", "Advanced")}
						</SegmentedControl.Item>
					</SegmentedControl.Root>
				}
			/>
			{fontMode === "simple" ? (
				<SettingsGroup>
					<FontSlotPicker
						label={t("settings.fonts.latin", "Latin font")}
						description={t(
							"settings.fonts.latinDesc",
							"Used for Latin glyphs; CJK glyphs fall through to the CJK font.",
						)}
						valueAtom={fontSimpleLatinAtom}
						presets={LATIN_FONT_PRESETS}
					/>
					<FontSlotPicker
						label={t("settings.fonts.cjk", "CJK font")}
						valueAtom={fontSimpleCjkAtom}
						presets={CJK_FONT_PRESETS}
					/>
				</SettingsGroup>
			) : (
				<Flex direction="column" gap="3">
					<SettingsGroup title={t("settings.fonts.editView", "Edit view")}>
						<FontSlotPicker
							label={t("settings.fonts.latin", "Latin font")}
							valueAtom={fontEditLatinAtom}
							presets={LATIN_FONT_PRESETS}
						/>
						<FontSlotPicker
							label={t("settings.fonts.cjk", "CJK font")}
							valueAtom={fontEditCjkAtom}
							presets={CJK_FONT_PRESETS}
						/>
					</SettingsGroup>
					<SettingsGroup title={t("settings.fonts.syncView", "Sync view")}>
						<FontSlotPicker
							label={t("settings.fonts.latin", "Latin font")}
							valueAtom={fontSyncLatinAtom}
							presets={LATIN_FONT_PRESETS}
						/>
						<FontSlotPicker
							label={t("settings.fonts.cjk", "CJK font")}
							valueAtom={fontSyncCjkAtom}
							presets={CJK_FONT_PRESETS}
						/>
					</SettingsGroup>
					<SettingsGroup title={t("settings.fonts.previewView", "Preview")}>
						<FontSlotPicker
							label={t("settings.fonts.latin", "Latin font")}
							valueAtom={fontPreviewLatinAtom}
							presets={LATIN_FONT_PRESETS}
						/>
						<FontSlotPicker
							label={t("settings.fonts.cjk", "CJK font")}
							valueAtom={fontPreviewCjkAtom}
							presets={CJK_FONT_PRESETS}
						/>
					</SettingsGroup>
				</Flex>
			)}
			<SettingsGroup title={t("settings.fonts.customTitle", "Custom fonts")}>
				<div style={{ padding: "12px 16px" }}>
					<CustomFontFiles />
				</div>
			</SettingsGroup>
			<Flex direction="column" gap="1">
				<Text size="1" color="gray">
					AaBbCcDd 漢字 ひらがな 한글
				</Text>
				{fontMode === "simple" ? (
					<Text size="2" style={{ fontFamily: stacks.edit }}>
						AaBbCcDd 漢字 ひらがな 한글 Preview
					</Text>
				) : (
					<>
						<Text size="2" style={{ fontFamily: stacks.edit }}>
							Edit — AaBb 漢字 ひらがな
						</Text>
						<Text size="2" style={{ fontFamily: stacks.sync }}>
							Sync — AaBb 漢字 ひらがな
						</Text>
						<Text size="2" style={{ fontFamily: stacks.preview }}>
							Preview — AaBb 漢字 ひらがな
						</Text>
					</>
				)}
			</Flex>
		</Flex>
	);
};
