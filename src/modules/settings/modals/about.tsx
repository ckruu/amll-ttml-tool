import { BUILD_TIME, GIT_COMMIT } from "virtual:buildmeta";
import {
	Card,
	Flex,
	Heading,
	Link,
	Text,
} from "@radix-ui/themes";
import { useTranslation } from "react-i18next";

export const SettingsAboutTab = () => {
	const { t } = useTranslation();

	return (
		<Flex direction="column" gap="4">
			<Flex direction="column" gap="1">
				<Heading size="4">
					{t("aboutModal.appName", "Apple Music-like lyrics TTML Tools")}
				</Heading>
				<Text as="div" size="2" color="gray">
					{t(
						"aboutModal.description",
						"一个用于 Apple Music-like lyrics 生态的逐词歌词 TTML 编辑和时间轴工具",
					)}
				</Text>
			</Flex>

			<Card>
				<Flex direction="column" gap="2">
					<Flex direction="column" gap="1">
						<Text as="div" size="2">
							{t("aboutModal.buildDate", "构建日期：{date}", {
								date: BUILD_TIME,
							})}
						</Text>
						<Text as="div" size="2">
							{t("aboutModal.gitCommit", "Git 提交：{commit}", {
								commit:
									GIT_COMMIT === "unknown" ? (
										t("aboutModal.unknown", "unknown")
									) : (
										<Link
											href={`https://github.com/amll-dev/amll-ttml-tool/commit/${GIT_COMMIT}`}
											target="_blank"
											rel="noreferrer"
										>
											{GIT_COMMIT}
										</Link>
									),
							})}
						</Text>
					</Flex>
				</Flex>
			</Card>
		</Flex>
	);
};
