import { useEffect, useState } from "react";

/**
 * Tracks document visibility. Visual loops (player frames, cursors, beat
 * analysis, spectrogram sync) should idle while hidden; the audio engine
 * itself is never paused by this hook.
 */
export function usePageVisible(): boolean {
	const [visible, setVisible] = useState(
		() =>
			typeof document === "undefined" ||
			document.visibilityState === "visible",
	);

	useEffect(() => {
		const onChange = () => {
			setVisible(document.visibilityState === "visible");
		};
		document.addEventListener("visibilitychange", onChange);
		return () => {
			document.removeEventListener("visibilitychange", onChange);
		};
	}, []);

	return visible;
}
