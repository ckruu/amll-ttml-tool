import { Checkbox, Flex, Slider, Text, TextField } from "@radix-ui/themes";
import { useAtom, useAtomValue } from "jotai";
import type { FC } from "react";
import { useId, useState } from "react";
import { useTranslation } from "react-i18next";
import {
	detectedRefreshHzAtom,
	previewViewFontSizeAtom,
	previewVsyncEnabledAtom,
	sidebarPreviewFontSizeAtom,
} from "$/modules/settings/states/preview";

/**
 * Shared preview-view rows, embedded directly in each ribbon's Views
 * section (or Preview mode's View section) so there is only one place
 * for panel visibility toggles and preview appearance controls.
 */
export const PreviewFontSizeField: FC<{ target: "sidebar" | "main" }> = ({
	target,
}) => {
	const { t } = useTranslation();
	const [sidebarSize, setSidebarSize] = useAtom(sidebarPreviewFontSizeAtom);
	const [mainSize, setMainSize] = useAtom(previewViewFontSizeAtom);
	const id = useId();

	const size = target === "sidebar" ? sidebarSize : mainSize;
	const setSize = target === "sidebar" ? setSidebarSize : setMainSize;

	// Draft text while typing, tagged with the atom value it started from so
	// external changes automatically fall back to showing the atom value.
	const [draft, setDraft] = useState<{ text: string; base: number } | null>(
		null,
	);
	const shown = draft && draft.base === size ? draft.text : String(size);

	const commit = (raw: string) => {
		const n = Number.parseInt(raw, 10);
		if (!Number.isFinite(n)) {
			setDraft(null);
			return;
		}
		setSize(Math.max(1, n));
		setDraft(null);
	};

	return (
		<Flex gap="3" align="center">
			<Text size="1" asChild>
				<label htmlFor={id} style={{ userSelect: "none" }}>
					{t("ribbonBar.previewView.fontSize", "预览字号")}
				</label>
			</Text>
			<TextField.Root
				type="number"
				size="1"
				min={1}
				step={1}
				id={id}
				value={shown}
				onChange={(e) => setDraft({ text: e.target.value, base: size })}
				onBlur={(e) => commit(e.target.value)}
				onKeyDown={(e) => {
					if (e.key === "Enter") commit(e.currentTarget.value);
					if (e.key === "Escape") setDraft(null);
				}}
				style={{ width: "5.5em" }}
			>
				<TextField.Slot>px</TextField.Slot>
			</TextField.Root>
		</Flex>
	);
};

export const PreviewVsyncField: FC = () => {
	const { t } = useTranslation();
	const [vsync, setVsync] = useAtom(previewVsyncEnabledAtom);
	const hz = useAtomValue(detectedRefreshHzAtom);
	const id = useId();

	return (
		<Flex gap="3" align="center">
			<Text size="1" asChild>
				<label htmlFor={id} style={{ userSelect: "none" }}>
					{t("ribbonBar.previewView.vsync", "垂直同步")}
				</label>
			</Text>
			<Checkbox
				id={id}
				checked={vsync}
				onCheckedChange={(v) => setVsync(!!v)}
			/>
			<Text wrap="nowrap" size="1" color="gray">
				{hz != null
					? t("ribbonBar.previewView.detectedHz", "检出 {hz} Hz", { hz })
					: t("ribbonBar.previewView.detecting", "检测中…")}
			</Text>
		</Flex>
	);
};

/**
 * Compact label + slider + value row for preview style/background settings.
 * Works in raw atom units; callers convert to display units.
 */
export const PreviewSliderRow: FC<{
	label: string;
	value: number;
	min: number;
	max: number;
	step?: number;
	onChange: (v: number) => void;
	display: string;
}> = ({ label, value, min, max, step = 1, onChange, display }) => {
	const id = useId();
	return (
		<Flex gap="2" align="center">
			<Text size="1" asChild style={{ minWidth: "7em" }}>
				<label htmlFor={id} style={{ userSelect: "none" }}>
					{label}
				</label>
			</Text>
			<Slider
				id={id}
				value={[value]}
				onValueChange={(v) => onChange(v[0])}
				min={min}
				max={max}
				step={step}
				style={{ width: "7em" }}
			/>
			<Text
				size="1"
				color="gray"
				wrap="nowrap"
				style={{ minWidth: "3em", fontVariantNumeric: "tabular-nums" }}
			>
				{display}
			</Text>
		</Flex>
	);
};

/** Label + checkbox row matching ribbon density. */
export const PreviewCheckRow: FC<{
	label: string;
	checked: boolean;
	onChange: (v: boolean) => void;
	disabled?: boolean;
}> = ({ label, checked, onChange, disabled }) => {
	const id = useId();
	return (
		<Flex gap="2" align="center">
			<Text size="1" asChild>
				<label htmlFor={id} style={{ userSelect: "none" }}>
					{label}
				</label>
			</Text>
			<Checkbox
				id={id}
				checked={checked}
				disabled={disabled}
				onCheckedChange={(v) => onChange(!!v)}
			/>
		</Flex>
	);
};
