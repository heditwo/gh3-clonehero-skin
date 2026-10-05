import argparse
import json
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image


def find_lines(image):
    image = image.convert("RGB")
    if abs(image.width / image.height - 16 / 9) > 0.001:
        raise ValueError("Use a complete 16:9 capture")
    if image.size != (1600, 900):
        image = image.resize((1600, 900), Image.Resampling.LANCZOS)
    pixels = np.asarray(image, dtype=float)
    profile = np.zeros(900)
    for y in range(410, 895):
        half_width = (y - 212) * 0.4
        row = pixels[y, round(803 - half_width):round(803 + half_width)]
        profile[y] = np.median(row.mean(axis=1))
    edges = np.diff(np.r_[False, profile > 25, False].astype(int))
    found = []
    for start, end in zip(np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)):
        peak = start + np.argmax(profile[start:end])
        if end - start > 20 or profile[peak] < 65:
            continue
        left = round(803 - (peak - 212) / 2 - 40)
        right = round(803 + (peak - 212) / 2 + 40)
        row = pixels[peak, left:right]
        mask = (row.mean(axis=1) > profile[peak] * 0.4) & (np.ptp(row, axis=1) < 20)
        spans = np.diff(np.r_[False, mask, False].astype(int))
        runs = [(s, e) for s, e in zip(np.flatnonzero(spans == 1), np.flatnonzero(spans == -1)) if e - s >= 8]
        if not runs or sum(e - s for s, e in runs) < (peak - 212) * 0.5:
            continue
        x0, x1 = left + runs[0][0], left + runs[-1][1] - 1
        if x1 - x0 < (peak - 212) * 0.75:
            continue
        width = int(x1 - x0 + 1)
        ink = profile[max(0, start - 1):end + 1].sum() / 255 / width * 1024
        if ink < 2:
            continue
        center = round((x0 + x1) / 2)
        offset = round(width * 0.08)
        crossing = pixels[peak, center - 2:center + 3].mean()
        face = np.median(np.r_[pixels[peak, center - offset - 2:center - offset + 3].mean(axis=1),
                               pixels[peak, center + offset - 2:center + offset + 3].mean(axis=1)])
        found.append({
            "y": float((start + end - 1) / 2), "left": int(x0), "right": int(x1),
            "width": width, "height": int(end - start), "ink": round(float(ink), 3),
            "string_crossing_ratio": round(float(crossing / max(face, 1)), 3),
        })
    return found


def read_frames(folder):
    result = []
    for path in sorted(Path(folder).glob("frame-*.png")):
        with Image.open(path) as image:
            result.extend(dict(file=path.name, **line) for line in find_lines(image))
    if not result:
        raise ValueError(f"No measurable lines in {folder}")
    return result


def track_video(path):
    command = [
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-ss", "2", "-i", str(path),
        "-t", "8", "-vf", "fps=30,scale=1600:900", "-f", "rawvideo", "-pix_fmt", "rgb24", "-",
    ]
    active, completed = [], []
    frame = 0
    with subprocess.Popen(command, stdout=subprocess.PIPE) as process:
        while raw := process.stdout.read(1600 * 900 * 3):
            if len(raw) != 1600 * 900 * 3:
                raise ValueError("Incomplete video frame")
            image = Image.frombytes("RGB", (1600, 900), raw)
            available = [line for line in find_lines(image) if line["y"] >= 540]
            pending = []
            for track in sorted(active, key=lambda item: -item["last_y"]):
                candidates = [line for line in available
                              if -3 <= line["y"] - track["last_y"] < 110 * (track["missing"] + 1)]
                if candidates:
                    match = min(candidates, key=lambda line: abs(line["y"] - track["last_y"]))
                    available.remove(match)
                    track["last_y"] = match["y"]
                    track["frames"] += 1
                    track["missing"] = 0
                    pending.append(track)
                elif track["missing"] < 2:
                    # Fret buttons hide the line briefly as it passes behind them.
                    track["missing"] += 1
                    pending.append(track)
                else:
                    completed.append(track)
            for line in available:
                pending.append({"first_frame": frame, "first_y": line["y"], "last_y": line["y"],
                                "frames": 1, "missing": 0})
            active = pending
            frame += 1
        if process.wait() != 0:
            raise ValueError("Video decode failed")
    checked = [track for track in completed if track["first_frame"] > 0 and track["frames"] >= 5]
    failed = [track for track in checked if track["last_y"] < 820]
    return {
        "frames": frame, "complete_tracks": len(checked), "early_disappearances": failed,
        "minimum_exit_y": min((track["last_y"] for track in checked), default=None), "tracks": checked,
    }


def main():
    parser = argparse.ArgumentParser(description="Compare beat lines with real GH3 frames at 16:9.")
    parser.add_argument("--reference", required=True, help="Folder with GH3 frame-*.png files")
    parser.add_argument("--candidate", required=True, help="Folder with skin frame-*.png files")
    parser.add_argument("--note-free-video", help="An 11-second or longer, 120 BPM capture with no PNG capture enabled")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    reference, candidate = read_frames(args.reference), read_frames(args.candidate)
    center = np.median([(line["left"] + line["right"]) / 2 for line in reference])
    reference = [line for line in reference if abs((line["left"] + line["right"]) / 2 - center) <= 1.5]
    # Notes can hide either endpoint. Fit the visible, centered lines with a robust slope.
    slopes = [(a["width"] - b["width"]) / (a["y"] - b["y"])
              for a in reference for b in reference if a["y"] - b["y"] > 80]
    if not slopes:
        raise ValueError("The GH3 frames must show lines at several depths")
    slope = np.median(slopes)
    intercept = np.median([line["width"] - slope * line["y"] for line in reference])
    evaluated = [line for line in candidate if 450 <= line["y"] <= 780
                 and abs((line["left"] + line["right"]) / 2 - center) <= 1.5]
    if not evaluated:
        raise ValueError("No complete lower-highway lines in the skin frames")
    residuals = [abs(line["width"] - slope * line["y"] - intercept) for line in evaluated]
    centers = [abs((line["left"] + line["right"]) / 2 - center) for line in candidate if 450 <= line["y"] <= 780]
    reference_ink = [line["ink"] for line in reference if 450 < line["y"] < 780 and 3 < line["ink"] < 7.2]
    candidate_ink = [line["ink"] for line in evaluated if 3 < line["ink"] < 7.2]
    if not reference_ink or not candidate_ink:
        raise ValueError("The captures must include regular beat lines")
    report = {
        "reference_lines": len(reference), "candidate_lines": len(candidate),
        "source_width_fit": [float(slope), float(intercept)], "source_center": float(center),
        "median_width_error_900p": float(np.median(residuals)),
        "p95_width_error_900p": float(np.percentile(residuals, 95)),
        "median_center_error_900p": float(np.median(centers)),
        "reference_minor_ink": float(np.median(reference_ink)),
        "candidate_minor_ink": float(np.median(candidate_ink)), "lines": candidate,
        "reference_string_crossing_ratio": float(np.median([line["string_crossing_ratio"] for line in reference])),
        "candidate_string_crossing_ratio": float(np.median([line["string_crossing_ratio"] for line in evaluated])),
    }
    if args.note_free_video:
        report["motion"] = track_video(args.note_free_video)
    Path(args.output).write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({key: value for key, value in report.items() if key not in ("lines", "motion")}, indent=2))
    if "motion" in report:
        print(json.dumps({key: value for key, value in report["motion"].items() if key != "tracks"}, indent=2))
    if report["p95_width_error_900p"] >= 5 or report["median_center_error_900p"] >= 2:
        raise SystemExit("FAIL: Line geometry differs from the GH3 reference")
    if abs(report["candidate_minor_ink"] / report["reference_minor_ink"] - 1) > 0.12:
        raise SystemExit("FAIL: Line thickness or brightness differs from the GH3 reference")
    if report["candidate_string_crossing_ratio"] >= 0.8:
        raise SystemExit("FAIL: Lines cover the strings at their crossing points")
    if "motion" in report:
        motion = report["motion"]
        if motion["complete_tracks"] < 12 or motion["early_disappearances"]:
            raise SystemExit("FAIL: Moving lines did not pass the fret row consistently")
    print("Beat-line checks passed")


if __name__ == "__main__":
    main()
