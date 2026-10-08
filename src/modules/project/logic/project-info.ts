import type { TTMLLyric } from "$/types/ttml";

export const ID_SOURCES = [
	{ key: "ncmMusicId", label: "NCM" },
	{ key: "qqMusicId", label: "QQ" },
	{ key: "spotifyId", label: "Spotify" },
	{ key: "appleMusicId", label: "Apple" },
	{ key: "isrc", label: "ISRC" },
];

export interface ProjectIdentity {
	displayName: string;
	isUntitled: boolean;
}

export function identifyProject(lyrics: TTMLLyric): ProjectIdentity {
	const getMeta = (key: string) =>
		lyrics.metadata.find((m) => m.key.toLowerCase() === key.toLowerCase())
			?.value[0];

	const title = getMeta("musicName")?.trim();
	const artist = getMeta("artists")?.trim();

	for (const source of ID_SOURCES) {
		const idValue = getMeta(source.key)?.trim();
		if (idValue) {
			if (title) {
				return {
					displayName: artist ? `${title} - ${artist}` : title,
					isUntitled: false,
				};
			}

			return {
				displayName: `Untitled (${source.label}: ${idValue})`,
				isUntitled: true,
			};
		}
	}

	if (title && artist) {
		return {
			displayName: `${title} - ${artist}`,
			isUntitled: false,
		};
	}
	if (title) {
		return {
			displayName: title,
			isUntitled: false,
		};
	}

	return {
		displayName: "Untitled Project",
		isUntitled: true,
	};
}
