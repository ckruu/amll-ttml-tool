import { Flex, Progress, Text, Tooltip } from "@radix-ui/themes";
import { useAtomValue } from "jotai";
import { useTranslation } from "react-i18next";
import { syncProgressAtom } from "$/modules/lyric-editor/utils/sync-progress";

export const SyncProgressBar = () => {
	const { t } = useTranslation();
	const progress = useAtomValue(syncProgressAtom);

	const label =
		progress.total === 0
			? t("header.syncProgress.empty", "No synchronizable words")
			: t("header.syncProgress.label", "{synced}/{total} synced ({percent}%)", {
					synced: progress.synced,
					total: progress.total,
					percent: progress.percent,
				});

	return (
		<Tooltip content={label}>
			<Flex align="center" gap="2" style={{ minWidth: 0 }}>
				<Progress
					value={progress.percent}
					max={100}
					size="1"
					style={{ width: 110 }}
					aria-label={label}
				/>
				<Text
					size="1"
					color="gray"
					style={{ whiteSpace: "nowrap", fontVariantNumeric: "tabular-nums" }}
				>
					{progress.total === 0
						? t("header.syncProgress.emptyShort", "—")
						: t("header.syncProgress.short", "{percent}%", {
								percent: progress.percent,
							})}
				</Text>
			</Flex>
		</Tooltip>
	);
};
