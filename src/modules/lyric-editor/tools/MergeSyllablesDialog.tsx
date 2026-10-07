import { PauseFilled, PlayFilled } from "@fluentui/react-icons";
import {
	Box,
	Button,
	Checkbox,
	Dialog,
	Flex,
	IconButton,
	SegmentedControl,
	Text,
	TextField,
	Tooltip,
} from "@radix-ui/themes";
import { useAtomValue, useSetAtom } from "jotai";
import {
	Fragment,
	memo,
	useCallback,
	useEffect,
	useMemo,
	useRef,
	useState,
} from "react";
import { useTranslation } from "react-i18next";
import { AMLLPlayerView } from "$/components/AMLLWrapper";
import { audioEngine } from "$/modules/audio/audio-engine";
import { AudioSlider } from "$/modules/audio/components/AudioSlider";
import { audioPlayingAtom } from "$/modules/audio/states";
import {
	previewViewFontSizeAtom,
	suspendBackgroundPreviewsAtom,
	usePreviewFpsCap,
} from "$/modules/settings/states/preview";
import { lyricLinesAtom } from "$/states/main.ts";
import styles from "./MergeSyllablesDialog.module.css";
import {
	buildLineMergeModel,
	clusterKey,
	type LineMergeModel,
	type MergeEditorDraft,
} from "./mergeDraft";

export type { LineMergeModel, MergeEditorDraft };
export { clusterKey, emptyMergeDraft, gapKey } from "./mergeDraft";

type ThresholdPreset = "5" | "15" | "30" | "custom";

const MergeGapEditor = memo(
	({
		words,
		joined,
		manual,
		onToggleGap,
		onJoinRange,
	}: {
		/** non-space word texts in order */
		words: string[];
		/** per-gap join state */
		joined: boolean[];
		/** per-gap manual-override flag */
		manual: boolean[];
		onToggleGap: (gap: number) => void;
		onJoinRange: (fromWord: number, toWord: number) => void;
	}) => {
		const [drag, setDrag] = useState<{
			anchor: number;
			extent: number;
		} | null>(null);
		const dragRef = useRef<{ anchor: number; extent: number } | null>(null);
		const suppressClick = useRef(false);

		const setDragBoth = useCallback(
			(d: { anchor: number; extent: number } | null) => {
				dragRef.current = d;
				setDrag(d);
			},
			[],
		);

		// Live drag range (word positions, inclusive). Pills stay computed
		// from committed `joined` so the DOM never restructures mid-drag
		// (which would kill the pointer sequence); the range only tints chips.
		const dragRange = useMemo(() => {
			if (!drag || drag.anchor === drag.extent) return null;
			return {
				lo: Math.min(drag.anchor, drag.extent),
				hi: Math.max(drag.anchor, drag.extent),
			};
		}, [drag]);

		// Group consecutive joined words into pills; pills split at cut gaps.
		// NOTE: computed from committed joins only — never from drag state.
		const pills = useMemo(() => {
			const list: number[][] = [];
			let run: number[] = [];
			words.forEach((_, i) => {
				if (i > 0 && !joined[i - 1]) {
					if (run.length > 0) list.push(run);
					run = [];
				}
				run.push(i);
			});
			if (run.length > 0) list.push(run);
			return list;
		}, [words, joined]);

		const endDrag = useCallback(
			(commit: boolean) => {
				const d = dragRef.current;
				dragRef.current = null;
				setDrag(null);
				if (d && commit && d.anchor !== d.extent) {
					onJoinRange(
						Math.min(d.anchor, d.extent),
						Math.max(d.anchor, d.extent),
					);
					// A drag-end pointerup is followed by a click; swallow it.
					suppressClick.current = true;
					setTimeout(() => {
						suppressClick.current = false;
					}, 0);
				}
			},
			[onJoinRange],
		);

		useEffect(() => {
			const up = () => endDrag(true);
			const cancel = () => endDrag(false);
			window.addEventListener("pointerup", up);
			window.addEventListener("pointercancel", cancel);
			return () => {
				window.removeEventListener("pointerup", up);
				window.removeEventListener("pointercancel", cancel);
			};
		}, [endDrag]);

		const gapTitle = (g: number) =>
			joined[g]
				? manual[g]
					? "joined (manual) — click to cut"
					: "joined (auto) — click to cut"
				: manual[g]
					? "cut (manual) — click to join"
					: "cut — click to join";

		const inDragRange = (pos: number) =>
			dragRange != null && pos >= dragRange.lo && pos <= dragRange.hi;

		const renderGap = (g: number) => (
			<button
				key={`gap-${g}`}
				type="button"
				className={styles.gapButton}
				data-joined={joined[g] || undefined}
				data-manual={manual[g] || undefined}
				onClick={() => {
					if (suppressClick.current) return;
					onToggleGap(g);
				}}
				aria-label={`gap ${g + 1}`}
				title={gapTitle(g)}
			/>
		);

		return (
			<div className={styles.editorBox} style={{ touchAction: "pan-y" }}>
				{pills.map((pill) => (
					<Fragment key={`pill-${pill[0]}`}>
						{pill[0] > 0 && renderGap(pill[0] - 1)}
						{pill.length > 1 ? (
							<span
								className={styles.pill}
								data-merged="true"
							>
								{pill.map((pos, k) => (
									<Fragment key={`w-${pos}`}>
										{k > 0 && renderGap(pos - 1)}
										<span
											className={styles.pillChip}
											data-dragging={inDragRange(pos) || undefined}
											onPointerDown={(e) => {
												if (e.button !== 0) return;
												e.preventDefault();
												setDragBoth({ anchor: pos, extent: pos });
											}}
											onPointerEnter={() => {
												if (dragRef.current) {
													setDragBoth({ ...dragRef.current, extent: pos });
												}
											}}
										>
											{words[pos]}
										</span>
									</Fragment>
								))}
							</span>
						) : (
							<span
								className={styles.chip}
								data-dragging={inDragRange(pill[0]) || undefined}
								onPointerDown={(e) => {
									if (e.button !== 0) return;
									e.preventDefault();
									setDragBoth({ anchor: pill[0], extent: pill[0] });
								}}
								onPointerEnter={() => {
									if (dragRef.current) {
										setDragBoth({
											...dragRef.current,
											extent: pill[0],
										});
									}
								}}
							>
								{words[pill[0]]}
							</span>
						)}
					</Fragment>
				))}
			</div>
		);
	},
);

const LineCard = memo(
	({
		model,
		mergeOn,
		lineSmoothOn,
		onToggleMerge,
		onToggleLineSmooth,
		onToggleGap,
		onJoinRange,
	}: {
		model: LineMergeModel;
		mergeOn: boolean;
		lineSmoothOn: boolean;
		onToggleMerge: (on: boolean) => void;
		onToggleLineSmooth: (on: boolean) => void;
		onToggleGap: (gap: number) => void;
		onJoinRange: (fromWord: number, toWord: number) => void;
	}) => {
		const { t } = useTranslation();
		const words = model.contentIndices.map(
			(wi) => model.line.words[wi]?.word ?? "",
		);
		return (
			<div className={styles.lineCard}>
				<Flex align="center" gap="3" mb="2">
					<Text size="2" weight="bold">
						{t("mergeDialog.lineN", "第 {n} 行", {
							n: model.lineIndex + 1,
						})}
					</Text>
					<Text as="label" size="1" ml="auto">
						<Flex gap="1" align="center">
							<Checkbox
								checked={mergeOn}
								onCheckedChange={(c) => onToggleMerge(Boolean(c))}
							/>
							{t("mergeDialog.mergeLine", "合并")}
						</Flex>
					</Text>
					<Text as="label" size="1">
						<Flex gap="1" align="center">
							<Checkbox
								checked={lineSmoothOn}
								onCheckedChange={(c) => onToggleLineSmooth(Boolean(c))}
							/>
							{t("mergeDialog.smoothLine", "平滑")}
						</Flex>
					</Text>
				</Flex>
				<MergeGapEditor
					words={words}
					joined={model.joined}
					manual={model.manual}
					onToggleGap={onToggleGap}
					onJoinRange={onJoinRange}
				/>
			</div>
		);
	},
);

export const MergeSyllablesDialog = ({
	open,
	onOpenChange,
	initial,
	lineIndices,
	onApply,
}: {
	open: boolean;
	onOpenChange: (open: boolean) => void;
	initial: MergeEditorDraft;
	/** sorted target line indices (excluding skipped lines) */
	lineIndices: number[];
	onApply: (draft: MergeEditorDraft) => void;
}) => {
	const { t } = useTranslation();
	const lyricLines = useAtomValue(lyricLinesAtom);
	const [thresholdPreset, setThresholdPreset] =
		useState<ThresholdPreset>("15");
	const [customThreshold, setCustomThreshold] = useState("15");
	const [userJoins, setUserJoins] = useState<Set<string>>(new Set());
	const [userCuts, setUserCuts] = useState<Set<string>>(new Set());
	const [lineMergeOff, setLineMergeOff] = useState<Set<number>>(new Set());
	const [smoothOffKeys, setSmoothOffKeys] = useState<Set<string>>(new Set());
	const [afterView, setAfterView] = useState(true);
	const [modalFontSize, setModalFontSize] = useState<number | null>(null);
	const [fontDraft, setFontDraft] = useState<{
		text: string;
		base: number;
	} | null>(null);
	const [split, setSplit] = useState(0.5);
	const splitDrag = useRef<{ x: number; split: number } | null>(null);
	const bodyRef = useRef<HTMLDivElement>(null);
	const setSuspendBackground = useSetAtom(suspendBackgroundPreviewsAtom);

	// While the merge editor is open it holds the only live player;
	// background preview instances unmount to save resources.
	useEffect(() => {
		if (!open) return;
		setSuspendBackground(true);
		return () => {
			setSuspendBackground(false);
		};
	}, [open, setSuspendBackground]);

	const parsedCustom = Number.parseFloat(customThreshold);
	const customInvalid =
		thresholdPreset === "custom" &&
		(Number.isNaN(parsedCustom) || parsedCustom < 0 || parsedCustom > 100);
	const threshold =
		thresholdPreset === "custom" ? parsedCustom : Number(thresholdPreset);

	// Initialize working state whenever the modal opens.
	useEffect(() => {
		if (!open) return;
		const t0 = initial.threshold;
		if (t0 === 5 || t0 === 15 || t0 === 30) {
			setThresholdPreset(String(t0) as ThresholdPreset);
		} else {
			setThresholdPreset("custom");
			setCustomThreshold(String(t0));
		}
		setUserJoins(new Set(initial.userJoins));
		setUserCuts(new Set(initial.userCuts));
		setLineMergeOff(new Set(initial.lineMergeOff));
		setSmoothOffKeys(new Set(initial.smoothOffKeys));
		setAfterView(true);
		setModalFontSize(null);
		setFontDraft(null);
	}, [open, initial]);

	// Auto-cluster layout shifts with the threshold: user gap overrides
	// persist, but stale per-cluster smooth flags are dropped.
	const handleThresholdPreset = (v: ThresholdPreset) => {
		setThresholdPreset(v);
		setSmoothOffKeys(new Set());
	};
	const handleCustomThreshold = (v: string) => {
		setCustomThreshold(v);
		setSmoothOffKeys(new Set());
	};

	const models = useMemo(() => {
		if (customInvalid || Number.isNaN(threshold)) return [];
		const draft = { threshold, userJoins, userCuts, lineMergeOff, smoothOffKeys };
		const list: LineMergeModel[] = [];
		for (const lineIndex of lineIndices) {
			const line = lyricLines.lyricLines[lineIndex];
			if (!line) continue;
			list.push(buildLineMergeModel(line, lineIndex, draft));
		}
		return list;
	}, [
		customInvalid,
		threshold,
		userJoins,
		userCuts,
		lineMergeOff,
		smoothOffKeys,
		lineIndices,
		lyricLines,
	]);

	const gapKeyOf = useCallback(
		(lineIndex: number, gap: number) =>
			`${lineIndex}:${gap}`,
		[],
	);

	const toggleGap = useCallback(
		(lineIndex: number, gap: number, joined: boolean) => {
			const key = gapKeyOf(lineIndex, gap);
			if (joined) {
				setUserCuts((prev) => new Set(prev).add(key));
				setUserJoins((prev) => {
					const next = new Set(prev);
					next.delete(key);
					return next;
				});
			} else {
				setUserJoins((prev) => new Set(prev).add(key));
				setUserCuts((prev) => {
					const next = new Set(prev);
					next.delete(key);
					return next;
				});
			}
		},
		[gapKeyOf],
	);

	const joinRange = useCallback(
		(lineIndex: number, fromWord: number, toWord: number) => {
			setUserJoins((prev) => {
				const next = new Set(prev);
				for (let g = fromWord; g < toWord; g++) {
					next.add(gapKeyOf(lineIndex, g));
				}
				return next;
			});
			setUserCuts((prev) => {
				const next = new Set(prev);
				for (let g = fromWord; g < toWord; g++) {
					next.delete(gapKeyOf(lineIndex, g));
				}
				return next;
			});
			setLineMergeOff((prev) => {
				const next = new Set(prev);
				next.delete(lineIndex);
				return next;
			});
		},
		[gapKeyOf],
	);

	const toggleLineMerge = useCallback((lineIndex: number, on: boolean) => {
		setLineMergeOff((prev) => {
			const next = new Set(prev);
			if (on) next.delete(lineIndex);
			else next.add(lineIndex);
			return next;
		});
	}, []);

	const toggleLineSmooth = useCallback(
		(lineIndex: number, clusterCount: number, on: boolean) => {
			setSmoothOffKeys((prev) => {
				const next = new Set(prev);
				for (let ci = 0; ci < clusterCount; ci++) {
					const key = clusterKey(lineIndex, ci);
					if (on) next.delete(key);
					else next.add(key);
				}
				return next;
			});
		},
		[],
	);

	const handleApply = () => {
		if (customInvalid) return;
		onApply({ threshold, userJoins, userCuts, lineMergeOff, smoothOffKeys });
		onOpenChange(false);
	};

	// Full-song merged draft for the preview player.
	const draftLines = useMemo(() => {
		const mergedByIndex = new Map(models.map((m) => [m.lineIndex, m.merged]));
		return lyricLines.lyricLines.map(
			(line, index) => mergedByIndex.get(index) ?? line,
		);
	}, [models, lyricLines]);

	const appPreviewFontSize = useAtomValue(previewViewFontSizeAtom);
	const effectivePreviewFontSize = modalFontSize ?? appPreviewFontSize;
	const fontShown =
		fontDraft && fontDraft.base === effectivePreviewFontSize
			? fontDraft.text
			: String(effectivePreviewFontSize);
	const commitFontSize = (raw: string) => {
		const n = Number.parseInt(raw, 10);
		if (!Number.isFinite(n)) {
			setFontDraft(null);
			return;
		}
		setModalFontSize(Math.max(1, n));
		setFontDraft(null);
	};

	const fpsCap = usePreviewFpsCap();
	const audioPlaying = useAtomValue(audioPlayingAtom);

	return (
		<Dialog.Root open={open} onOpenChange={onOpenChange}>
			<Dialog.Content maxWidth="880px">
				<Dialog.Title>
					{t("mergeDialog.title", "合并音节")}
				</Dialog.Title>

				<div ref={bodyRef} className={styles.splitBody}>
					<Flex
						direction="column"
						gap="3"
						style={{ flex: `${split} 1 0`, minWidth: 0 }}
					>
						<Flex align="center" gap="3" wrap="wrap">
							<Text size="2" weight="bold">
								{t("mergeDialog.threshold", "平滑阈值")}
							</Text>
							<SegmentedControl.Root
								size="1"
								value={thresholdPreset}
								onValueChange={(v) =>
									handleThresholdPreset(v as ThresholdPreset)
								}
							>
								<SegmentedControl.Item value="5">5%</SegmentedControl.Item>
								<SegmentedControl.Item value="15">15%</SegmentedControl.Item>
								<SegmentedControl.Item value="30">30%</SegmentedControl.Item>
								<SegmentedControl.Item value="custom">
									{t("mergeDialog.custom", "自定")}
								</SegmentedControl.Item>
							</SegmentedControl.Root>
							{thresholdPreset === "custom" && (
								<TextField.Root
									size="1"
									type="number"
									min="0"
									max="100"
									value={customThreshold}
									onChange={(e) => handleCustomThreshold(e.target.value)}
									color={customInvalid ? "red" : undefined}
									style={{ width: "76px" }}
								>
									<TextField.Slot>%</TextField.Slot>
								</TextField.Root>
							)}
						</Flex>

						<Box
							style={{
								maxHeight: "46vh",
								overflowY: "auto",
								border: "1px solid var(--gray-a5)",
								borderRadius: "var(--radius-3)",
								padding: "8px",
							}}
						>
							<Flex direction="column" gap="2">
								{models.length === 0 && (
									<Text size="2" color="gray">
										{t("mergeDialog.noLines", "所选范围内没有可合并的歌词行。")}
									</Text>
								)}
								{models.map((m) => {
									const mergeOn = !lineMergeOff.has(m.lineIndex);
									const clusters = m.clusters;
									const lineSmoothOn =
										clusters.length === 0 ||
										clusters.every(
											(_, ci) =>
												!smoothOffKeys.has(clusterKey(m.lineIndex, ci)),
										);
									return (
										<LineCard
											key={m.lineIndex}
											model={m}
											mergeOn={mergeOn}
											lineSmoothOn={lineSmoothOn}
											onToggleMerge={(on) => toggleLineMerge(m.lineIndex, on)}
											onToggleLineSmooth={(on) =>
												toggleLineSmooth(m.lineIndex, clusters.length, on)
											}
											onToggleGap={(gap) =>
												toggleGap(
													m.lineIndex,
													gap,
													Boolean(m.joined[gap]),
												)
											}
											onJoinRange={(fromWord, toWord) =>
												joinRange(m.lineIndex, fromWord, toWord)
											}
										/>
									);
								})}
							</Flex>
						</Box>
					</Flex>

					<div
						className={styles.divider}
						onPointerDown={(e) => {
							e.currentTarget.setPointerCapture(e.pointerId);
							splitDrag.current = { x: e.clientX, split };
						}}
						onPointerMove={(e) => {
							const d = splitDrag.current;
							const body = bodyRef.current;
							if (!d || !body) return;
							const rect = body.getBoundingClientRect();
							if (rect.width <= 0) return;
							const next =
								d.split + (e.clientX - d.x) / rect.width;
							setSplit(Math.min(0.7, Math.max(0.3, next)));
						}}
						onPointerUp={() => {
							splitDrag.current = null;
						}}
						onPointerCancel={() => {
							splitDrag.current = null;
						}}
					/>
					<Flex
						direction="column"
						gap="2"
						style={{ flex: `${1 - split} 1 0`, minWidth: 0 }}
					>
						<Flex gap="2" align="center" wrap="nowrap">
							<SegmentedControl.Root
								size="1"
								value={afterView ? "after" : "before"}
								onValueChange={(v) => setAfterView(v === "after")}
							>
								<SegmentedControl.Item value="after">
									{t("mergeDialog.after", "合并后")}
								</SegmentedControl.Item>
								<SegmentedControl.Item value="before">
									{t("mergeDialog.before", "合并前")}
								</SegmentedControl.Item>
							</SegmentedControl.Root>
							<Button
								size="1"
								variant={modalFontSize == null ? "soft" : "ghost"}
								color="gray"
								onClick={() => {
									setModalFontSize(null);
									setFontDraft(null);
								}}
								disabled={modalFontSize == null}
							>
								{t("mergeDialog.auto", "自动")}
							</Button>
							<TextField.Root
								size="1"
								type="number"
								min={1}
								step={1}
								value={fontShown}
								onChange={(e) =>
									setFontDraft({
										text: e.target.value,
										base: effectivePreviewFontSize,
									})
								}
								onBlur={(e) => commitFontSize(e.target.value)}
								onKeyDown={(e) => {
									if (e.key === "Enter") commitFontSize(e.currentTarget.value);
									if (e.key === "Escape") setFontDraft(null);
								}}
								style={{ width: "4.5em", flexShrink: 0 }}
								aria-label={t("mergeDialog.previewFontSize", "预览字号")}
							>
								<TextField.Slot>px</TextField.Slot>
							</TextField.Root>
						</Flex>
						<Box
							style={{ flexGrow: 1, minHeight: "220px" }}
						>
							<AMLLPlayerView
								lines={afterView ? draftLines : lyricLines.lyricLines}
								fontSizePx={effectivePreviewFontSize}
								fpsCap={fpsCap}
								suspendable={false}
							/>
						</Box>
						<Flex gap="2" align="center">
							<Tooltip
								content={
									audioPlaying
										? t("mergeDialog.pause", "暂停")
										: t("mergeDialog.play", "播放")
								}
							>
								<IconButton
									size="2"
									variant="soft"
									onClick={() => {
										if (audioEngine.musicPlaying) {
											audioEngine.pauseMusic();
										} else {
											audioEngine.resumeMusic();
										}
									}}
									aria-label={
										audioPlaying
											? t("mergeDialog.pause", "暂停")
											: t("mergeDialog.play", "播放")
									}
								>
									{audioPlaying ? <PauseFilled /> : <PlayFilled />}
								</IconButton>
							</Tooltip>
							<Box flexGrow="1" minWidth="0">
								<AudioSlider />
							</Box>
						</Flex>
					</Flex>
				</div>

				<Flex gap="3" mt="4" justify="end">
					<Button
						variant="soft"
						color="gray"
						onClick={() => onOpenChange(false)}
					>
						{t("common.cancel", "取消")}
					</Button>
					<Button disabled={customInvalid} onClick={handleApply}>
						{t("mergeDialog.applyDraft", "应用到平滑设置")}
					</Button>
				</Flex>
			</Dialog.Content>
		</Dialog.Root>
	);
};
