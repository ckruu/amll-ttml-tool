
import { Card, Inset } from "@radix-ui/themes";
import { AnimatePresence } from "framer-motion";
import { useAtomValue } from "jotai";
import { forwardRef, lazy, memo } from "react";
import SuspensePlaceHolder from "$/components/SuspensePlaceHolder";
import { ToolMode, toolModeAtom } from "$/states/main.ts";

const EditModeRibbonBar = lazy(() => import("./edit-mode"));
const SyncModeRibbonBar = lazy(() => import("./sync-mode"));
const PreviewModeRibbonBar = lazy(() => import("./preview-mode"));

export const RibbonBar = memo(
	forwardRef<HTMLDivElement>((_props, ref) => {
		const toolMode = useAtomValue(toolModeAtom);

		return (
			<Card
				m="2"
				mb="0"
				style={{
					minHeight: "fit-content",
					flexShrink: "0",
				}}
				ref={ref}
			>
				<Inset>
					<div
						style={{
							height: "130px",
							overflowY: "clip",
						}}
					>
						<AnimatePresence mode="wait">
							{toolMode === ToolMode.Edit && (
								<SuspensePlaceHolder key="edit">
									<EditModeRibbonBar />
								</SuspensePlaceHolder>
							)}
							{toolMode === ToolMode.Sync && (
								<SuspensePlaceHolder key="sync">
									<SyncModeRibbonBar />
								</SuspensePlaceHolder>
							)}
							{toolMode === ToolMode.Preview && (
								<SuspensePlaceHolder key="preview">
									<PreviewModeRibbonBar />
								</SuspensePlaceHolder>
							)}
						</AnimatePresence>
					</div>
				</Inset>
			</Card>
		);
	}),
);

export default RibbonBar;
