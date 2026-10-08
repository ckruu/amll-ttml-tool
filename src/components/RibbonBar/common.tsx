
import { Flex, Separator, Text } from "@radix-ui/themes";
import { motion } from "framer-motion";
import {
	type FC,
	forwardRef,
	type PropsWithChildren,
	useImperativeHandle,
	useRef,
} from "react";

export const RibbonSection: FC<PropsWithChildren<{ label: string }>> = ({
	children,
	label,
}) => (
	<>
		<Flex
			direction="column"
			gap="1"
			flexShrink="0"
			style={{
				alignSelf: "stretch",
			}}
		>
			<Flex flexGrow="1" align="center" justify="center">
				{children}
			</Flex>
			<Text align="center" wrap="nowrap" color="gray" size="1">
				{label}
			</Text>
		</Flex>
		<Separator
			orientation="vertical"
			size="4"
			style={{ height: "unset", alignSelf: "stretch", minWidth: "1px" }}
		/>
	</>
);

export const RibbonFrame = forwardRef<HTMLDivElement, PropsWithChildren>(
	({ children }, ref) => {
		const frameRef = useRef<HTMLDivElement>(null);

		useImperativeHandle(ref, () => frameRef.current as HTMLDivElement, []);

		return (
			<Flex
				p="3"
				direction="row"
				gap="3"
				align="center"
				style={{
					overflowX: "auto",
					overflowY: "visible",
					height: "100%",
				}}
				asChild
			>
				<motion.div
					initial={{ x: 10, opacity: 0 }}
					animate={{ x: 0, opacity: 1 }}
					exit={{ x: -10, opacity: 0 }}
					layout
					ref={frameRef}
				>
					{children}
				</motion.div>
			</Flex>
		);
	},
);
