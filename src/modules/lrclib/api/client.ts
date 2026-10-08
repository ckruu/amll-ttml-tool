import { lrcLibLogger } from "../logger";
import type { LrcLibTrack } from "../types";

const BASE_URL = "https://lrclib.net/api";

export const LrcLibApi = {
	async search(query: string): Promise<LrcLibTrack[]> {
		if (!query.trim()) return [];

		try {
			const response = await fetch(
				`${BASE_URL}/search?q=${encodeURIComponent(query)}`,
			);
			if (!response.ok) {
				throw new Error(`LRCLIB Search failed: ${response.statusText}`);
			}
			const data = (await response.json()) as LrcLibTrack[];
			return data;
		} catch (error) {
			lrcLibLogger.error("LRCLIB API Error:", error);
			throw error;
		}
	},

	async getById(id: number): Promise<LrcLibTrack> {
		try {
			const response = await fetch(`${BASE_URL}/get/${id}`);
			if (!response.ok) {
				throw new Error(`LRCLIB Get failed: ${response.statusText}`);
			}
			return (await response.json()) as LrcLibTrack;
		} catch (error) {
			lrcLibLogger.error("LRCLIB API Error:", error);
			throw error;
		}
	},
};
