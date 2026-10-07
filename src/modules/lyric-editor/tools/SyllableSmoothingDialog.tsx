import { Dismiss16Regular, Info16Regular } from "@fluentui/react-icons";
import {
	Box,
	Button,
	Callout,
	Checkbox,
	Dialog,
	Flex,
	IconButton,
	RadioGroup,
	Text,
	TextField,
} from "@radix-ui/themes";
import { useAtom, useAtomValue } from "jotai";
import { useSetImmerAtom } from "jotai-immer";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
	DialogScopeSelector,
	useDialogScope,
} from "$/hooks/useDialogScope.tsx";
import {
	smoothSyllables,
} from "$/modules/segmentation/utils/syllable-smoothing.ts";
import {
	hasDismissedSyllableSmoothingTipAtom,
	syllableSmoothingDialogAtom,
} from "$/states/dialogs.ts";
import { lyricLinesAtom } from "$/states/main.ts";
import {
	applyDraftToLine,
	buildLineMergeModel,
	type MergeEditorDraft,
} from "./mergeDraft";
import { MergeSyllablesDialog } from "./MergeSyllablesDialog";

type ThresholdPreset = "5" | "15" | "30" | "custom";

export const SyllableSmoothingDialog = () => {
	const { t } = useTranslation();
	const [open, setOpen] = useAtom(syllableSmoothingDialogAtom);
	const [hasDismissedTip, setHasDismissedTip] = useAtom(
		hasDismissedSyllableSmoothingTipAtom,
	);
	const setLyricLines = useSetImmerAtom(lyricLinesAtom);
	const lyricLines = useAtomValue(lyricLinesAtom);
	const scopeState = useDialogScope(open);

	const [thresholdPreset, setThresholdPreset] = useState<ThresholdPreset>("15");
	const [customThreshold, setCustomThreshold] = useState("15");
	const [mergeSyllables, setMergeSyllables] = useState(false);
	const [mergeOpen, setMergeOpen] = useState(false);

	// Per-line smoothing inclusion + merge editor draft.
	const [excludedLines, setExcludedLines] = useState<Set<number>>(new Set());
	const [userJoins, setUserJoins] = useState<Set<string>>(new Set());
	const [userCuts, setUserCuts] = useState<Set<string>>(new Set());
	const [lineMergeOff, setLineMergeOff] = useState<Set<number>>(new Set());
	const [smoothOffKeys, setSmoothOffKeys] = useState<Set<string>>(new Set());

	const parsedCustom = parseFloat(customThreshold);
	const isCustomInvalid =
		thresholdPreset === "custom" &&
		(Number.isNaN(parsedCustom) || parsedCustom < 0 || parsedCustom > 100);

	const finalThreshold =
		thresholdPreset === "custom" ? parsedCustom : Number(thresholdPreset);

	// Auto-cluster layout shifts with the threshold: user gap overrides are
	// threshold-robust and persist, but stale per-cluster smooth flags reset.
	const resetStaleSmoothFlags = () => {
		setSmoothOffKeys(new Set());
	};

	useEffect(() => {
		if (open) {
			setExcludedLines(new Set());
			setUserJoins(new Set());
			setUserCuts(new Set());
			setLineMergeOff(new Set());
			setSmoothOffKeys(new Set());
		}
	}, [open ]);

	const targetLineIndices = useMemo(
		() => scopeState.getTargetLineIndices(),
		[scopeState.getTargetLineIndices],
	);

	const mergeDraft = useMemo<MergeEditorDraft>(
		() => ({
			threshold: finalThreshold,
			userJoins,
			userCuts,
			lineMergeOff,
			smoothOffKeys,
		}),
		[finalThreshold, userJoins, userCuts, lineMergeOff, smoothOffKeys],
	);

	const editableLineIndices = useMemo(
		() =>
			[...targetLineIndices]
				.filter((i) => !excludedLines.has(i))
				.sort((a, b) => a - b),
		[targetLineIndices, excludedLines],
	);

	const lineModels = useMemo(() => {
		if (!mergeSyllables || isCustomInvalid || Number.isNaN(finalThreshold)) {
			return [];
		}
		const list: ReturnType<typeof buildLineMergeModel>[] = [];
		for (const lineIndex of editableLineIndices) {
			const line = lyricLines.lyricLines[lineIndex];
			if (!line) continue;
			list.push(buildLineMergeModel(line, lineIndex, mergeDraft));
		}
		return list;
	}, [
		mergeSyllables,
		isCustomInvalid,
		finalThreshold,
		editableLineIndices,
		lyricLines,
		mergeDraft,
	]);

	const mergedLineCount = lineModels.filter((m) =>
		m.groups.some((g) => g.length > 1),
	).length;
	const customized =
		userJoins.size > 0 ||
		userCuts.size > 0 ||
		lineMergeOff.size > 0 ||
		smoothOffKeys.size > 0;

	const toggleLine = (lineIndex: number, checked: boolean) => {
		setExcludedLines((prev) => {
			const next = new Set(prev);
			if (checked) next.delete(lineIndex);
			else next.add(lineIndex);
			return next;
		});
	};

	const handleMergeApply = (draft: MergeEditorDraft) => {
		if (
			draft.threshold === 5 ||
			draft.threshold === 15 ||
			draft.threshold === 30
		) {
			setThresholdPreset(String(draft.threshold) as ThresholdPreset);
		} else {
			setThresholdPreset("custom");
			setCustomThreshold(String(draft.threshold));
		}
		setUserJoins(draft.userJoins);
		setUserCuts(draft.userCuts);
		setLineMergeOff(draft.lineMergeOff);
		setSmoothOffKeys(draft.smoothOffKeys);
	};

	const handleConfirm = () => {
		if (isCustomInvalid) return;

		setLyricLines((draft) => {
			draft.lyricLines.forEach((line, index) => {
				if (!targetLineIndices.has(index)) return;
				if (excludedLines.has(index)) return;
				if (!mergeSyllables) {
					draft.lyricLines[index] = smoothSyllables(line, {
						threshold: finalThreshold,
					});
					return;
				}
				draft.lyricLines[index] = applyDraftToLine(line, index, mergeDraft);
			});
		});

		setOpen(false);
	};

	return (
		<Dialog.Root open={open} onOpenChange={setOpen}>
			<Dialog.Content maxWidth="560px">
				<Dialog.Title>
					{t("syllableSmoothingDialog.title", "平滑时间轴")}
				</Dialog.Title>
				<Dialog.Description
					size="2"
					mb="2"
					color="gray"
					style={{ whiteSpace: "pre-line", lineHeight: "2" }}
				>
					{t(
						"syllableSmoothingDialog.description",
						"对 CJK 音节的时间轴进行平滑处理\n不会平滑其他语言的音节",
					)}
				</Dialog.Description>
				<Flex direction="column" gap="4">
					{!hasDismissedTip && (
						<Callout.Root
							color="amber"
							size="1"
							variant="soft"
							style={{
								display: "flex",
								alignItems: "center",
								paddingRight: "10px",
							}}
						>
							<Callout.Icon style={{ display: "flex", alignItems: "center" }}>
								<Info16Regular style={{ display: "block" }} />
							</Callout.Icon>

							<Callout.Text size="1" style={{ flex: 1 }}>
								{t(
									"syllableSmoothingDialog.thresholdWarning",
									"过高的阈值可能会导致过度拉伸音节，影响时间戳的准确性",
								)}
							</Callout.Text>

							<IconButton
								size="1"
								variant="ghost"
								color="gray"
								onClick={() => setHasDismissedTip(true)}
								style={{
									margin: 0,
									display: "flex",
									alignItems: "center",
									justifyContent: "center",
								}}
							>
								<Dismiss16Regular />
							</IconButton>
						</Callout.Root>
					)}

					<Flex direction="column" gap="2">
						<Text size="2" weight="bold">
							{t("syllableSmoothingDialog.thresholdLabel", "平滑阈值")}
						</Text>
						<RadioGroup.Root
							value={thresholdPreset}
							onValueChange={(v) => {
								setThresholdPreset(v as ThresholdPreset);
								resetStaleSmoothFlags();
							}}
						>
							<Flex direction="column" gap="2">
								<RadioGroup.Item value="5">
									{t("syllableSmoothingDialog.thresholdLow", "低 (5%)")}
								</RadioGroup.Item>
								<RadioGroup.Item value="15">
									{t("syllableSmoothingDialog.thresholdMedium", "中 (15%)")}
								</RadioGroup.Item>
								<RadioGroup.Item value="30">
									{t("syllableSmoothingDialog.thresholdHigh", "高 (30%)")}
								</RadioGroup.Item>
								<RadioGroup.Item value="custom">
									{t("syllableSmoothingDialog.thresholdCustom", "自定义")}
								</RadioGroup.Item>
							</Flex>
						</RadioGroup.Root>
						{thresholdPreset === "custom" && (
							<Flex align="center" gap="2" ml="4">
								<Text size="2">
									{t("syllableSmoothingDialog.customThresholdInput", "阈值")}
								</Text>
								<TextField.Root
									style={{ width: "80px" }}
									size="1"
									type="number"
									min="0"
									max="100"
									value={customThreshold}
									onChange={(e) => {
										setCustomThreshold(e.target.value);
										resetStaleSmoothFlags();
									}}
									color={isCustomInvalid ? "red" : undefined}
								>
									<TextField.Slot />
									<TextField.Slot>%</TextField.Slot>
								</TextField.Root>
							</Flex>
						)}
					</Flex>

					<Flex direction="column" gap="1">
						<Flex gap="2" align="center">
							<Text size="2" weight="bold" style={{ flex: 1 }}>
								{t("syllableSmoothingDialog.mergeSyllables", "合并音节")}
							</Text>
							<Button
								size="1"
								variant={mergeSyllables ? "soft" : "outline"}
								disabled={isCustomInvalid}
								onClick={() => {
									setMergeSyllables(true);
									setMergeOpen(true);
								}}
							>
								{t("syllableSmoothingDialog.customizeMerge", "自定义合并…")}
							</Button>
							{mergeSyllables && (
								<Button
									size="1"
									variant="ghost"
									color="gray"
									onClick={() => setMergeSyllables(false)}
								>
									{t("syllableSmoothingDialog.disableMerge", "不合并")}
								</Button>
							)}
						</Flex>
						<Text size="1" color="gray">
							{t(
								"syllableSmoothingDialog.mergeSyllablesHint",
								"可以获得类似 Apple Music 的、把 CJK 合并到一起的歌词，但不适合日常使用",
							)}
						</Text>
					</Flex>

					{mergeSyllables && (
						<Flex direction="column" gap="2">
							<Text size="1" color="gray">
								{t(
									"syllableSmoothingDialog.mergeSummary",
									"{count} 行会被合并{customized}，可在合并编辑器中逐字调整。",
									{
										count: mergedLineCount,
										customized: customized
											? t(
													"syllableSmoothingDialog.mergeCustomized",
													"（已自定义）",
												)
											: "",
									},
								)}
							</Text>
							<Box
								style={{
									maxHeight: "180px",
									overflowY: "auto",
									border: "1px solid var(--gray-a5)",
									borderRadius: "var(--radius-3)",
									padding: "8px",
								}}
							>
								<Flex direction="column" gap="2">
									{lineModels.length === 0 && (
										<Text size="1" color="gray">
											{t(
												"syllableSmoothingDialog.previewEmpty",
												"所选范围内没有歌词行。",
											)}
										</Text>
									)}
									{lineModels.map((m) => {
										const lineOn = !excludedLines.has(m.lineIndex);
										const willMerge = m.groups.some((g) => g.length > 1);
										return (
											<Flex
												key={m.lineIndex}
												align="center"
												gap="2"
												style={{ opacity: lineOn ? 1 : 0.55 }}
											>
												<Checkbox
													checked={lineOn}
													onCheckedChange={(c) =>
														toggleLine(m.lineIndex, Boolean(c))
													}
												/>
												<Text size="2" weight="bold">
													{t("syllableSmoothingDialog.lineN", "第 {n} 行", {
														n: m.lineIndex + 1,
													})}
												</Text>
												<Text size="1" color="gray" truncate>
													{m.line.words.map((w) => w.word).join("")}
													{willMerge
														? `  →  ${m.merged.words.map((w) => w.word).join("")}`
														: ""}
												</Text>
											</Flex>
										);
									})}
								</Flex>
							</Box>
						</Flex>
					)}

					<DialogScopeSelector {...scopeState} />
				</Flex>

				<MergeSyllablesDialog
					open={mergeOpen}
					onOpenChange={setMergeOpen}
					initial={mergeDraft}
					lineIndices={editableLineIndices}
					onApply={handleMergeApply}
				/>

				<Flex gap="3" mt="5" justify="end">
					<Dialog.Close>
						<Button variant="soft" color="gray">
							{t("common.cancel", "取消")}
						</Button>
					</Dialog.Close>
					<Button disabled={isCustomInvalid} onClick={handleConfirm}>
						{t("common.apply", "应用")}
					</Button>
				</Flex>
			</Dialog.Content>
		</Dialog.Root>
	);
};
