import { Box, Button, Flex, TextField, Tooltip } from "@radix-ui/themes";
import { lazy, type FC } from "react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useAtom, useAtomValue } from "jotai";
import SuspensePlaceHolder from "$/components/SuspensePlaceHolder";
import {
	effectiveSidebarPreviewFontSizeAtom,
	sidebarPreviewPanelFontSizeAtom,
	usePreviewFpsCap,
} from "$/modules/settings/states/preview";

const AMLLWrapper = lazy(() => import("$/components/AMLLWrapper"));

const MIN_SIZE = 1;

const clampSize = (n: number) => Math.max(MIN_SIZE, n);

/**
 * Preview tab header controls (rendered in the sidebar top row):
 * Auto-follow toggle + manual font-size field.
 */
export const PreviewHeaderControls: FC = () => {
	const { t } = useTranslation();
	const [panelSize, setPanelSize] = useAtom(sidebarPreviewPanelFontSizeAtom);
	const effectiveSize = useAtomValue(effectiveSidebarPreviewFontSizeAtom);

	// Draft text while typing, tagged with the effective size it started
	// from so external changes automatically fall back to showing it.
	const [draft, setDraft] = useState<{ text: string; base: number } | null>(
		null,
	);
	const shown =
		draft && draft.base === effectiveSize ? draft.text : String(effectiveSize);

	const commit = (raw: string) => {
		const n = Number.parseInt(raw, 10);
		if (!Number.isFinite(n)) {
			setDraft(null);
			return;
		}
		setPanelSize(clampSize(n));
		setDraft(null);
	};

	return (
		<Flex align="center" gap="1" flexShrink="0">
			<Tooltip content={t("sidebar.preview.followRibbon", "Follow ribbon size")}>
				<Button
					size="1"
					variant="ghost"
					color="gray"
					disabled={panelSize == null}
					onClick={() => setPanelSize(null)}
				>
					{t("sidebar.preview.followRibbonShort", "Auto")}
				</Button>
			</Tooltip>
			<TextField.Root
				type="number"
				size="1"
				min={MIN_SIZE}
				step={1}
				value={shown}
				onChange={(e) =>
					setDraft({ text: e.target.value, base: effectiveSize })
				}
				onBlur={(e) => commit(e.target.value)}
				onKeyDown={(e) => {
					if (e.key === "Enter") commit(e.currentTarget.value);
					if (e.key === "Escape") setDraft(null);
				}}
				style={{ width: "5em" }}
				aria-label={t("sidebar.preview.fontSizeLabel", "Preview font size")}
			>
				<TextField.Slot>px{panelSize == null ? "" : " •"}</TextField.Slot>
			</TextField.Root>
		</Flex>
	);
};

/**
 * Sidebar-hosted live preview.
 *
 * Reuses the AMLLWrapper core (reads lyricLinesAtom directly, so it stays
 * in sync while editing / syncing). Lazy-loaded so the player bundle is
 * only fetched when the tab is first opened.
 *
 * Font size is controlled from the header ({@link PreviewHeaderControls});
 * the override persists, and clearing it follows the ribbon View-section size.
 */
export const PreviewPanel: FC = () => {
	const effectiveSize = useAtomValue(effectiveSidebarPreviewFontSizeAtom);
	const fpsCap = usePreviewFpsCap();

	return (
		<Flex direction="column" height="100%">
			<Box flexGrow="1" minHeight="0" overflow="hidden" p="2">
				<SuspensePlaceHolder>
					<AMLLWrapper fontSizePx={effectiveSize} fpsCap={fpsCap} />
				</SuspensePlaceHolder>
			</Box>
		</Flex>
	);
};
