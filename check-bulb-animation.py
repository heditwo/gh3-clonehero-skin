#!/usr/bin/env python3
import argparse
import json
import subprocess
from pathlib import Path

import cv2
import numpy as np


def read_frames(path):
    command = ['ffmpeg', '-hide_banner', '-loglevel', 'error', '-i', str(path), '-t', '12',
               '-vf', 'fps=60,scale=1600:900,crop=480:420:1000:470',
               '-pix_fmt', 'bgr24', '-f', 'rawvideo', '-']
    frames = []
    with subprocess.Popen(command, stdout=subprocess.PIPE) as process:
        while raw := process.stdout.read(480 * 420 * 3):
            if len(raw) != 480 * 420 * 3:
                raise ValueError('Incomplete video frame')
            frames.append(np.frombuffer(raw, dtype=np.uint8).reshape(420, 480, 3).copy())
        if process.wait():
            raise ValueError('Video decode failed')
    return frames


def first_ready(frames):
    for index, frame in enumerate(frames):
        blue, green, red = frame[80:215, 95:205].astype(np.int16).transpose(2, 0, 1)
        if np.count_nonzero((blue > red + 35) & (green > red + 25) & (blue > 130)) > 1000:
            return index
    raise ValueError('No Star Power ready transition found')


def measure(frames, start):
    detector = cv2.SIFT_create(contrastThreshold=0.01)
    reference = cv2.cvtColor(frames[start + 65], cv2.COLOR_BGR2GRAY)
    mask = np.zeros(reference.shape, np.uint8)
    mask[170:350, 110:335] = 255
    mask[182:285, 175:265] = 0
    keypoints, descriptors = detector.detectAndCompute(reference, mask)
    matcher = cv2.BFMatcher()
    result = []
    for frame in frames[start:start + 55]:
        points, desc = detector.detectAndCompute(cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY), None)
        matches = [a for a, b in matcher.knnMatch(descriptors, desc, k=2) if a.distance < b.distance * 0.75]
        source = np.float32([keypoints[m.queryIdx].pt for m in matches])
        target = np.float32([points[m.trainIdx].pt for m in matches])
        matrix, inliers = cv2.estimateAffinePartial2D(source, target, ransacReprojThreshold=1.5)
        if matrix is None or inliers.sum() < 20:
            raise ValueError('Cannot measure the meter pose in a captured frame')
        pivot = matrix @ np.array([105, 157, 1])
        result.append({'scale': float(np.hypot(matrix[0, 0], matrix[1, 0])),
                       'angle': float(np.rad2deg(np.arctan2(matrix[1, 0], matrix[0, 0]))),
                       'x': float(pivot[0]-105), 'y': float(pivot[1]-157),
                       'matches': int(inliers.sum())})
    return result


def main():
    parser = argparse.ArgumentParser(description='Compare the GH3 and skin bulb-entrance meter motion.')
    parser.add_argument('reference', type=Path)
    parser.add_argument('capture', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    reference_frames = read_frames(args.reference)
    capture_frames = read_frames(args.capture)
    reference_start = first_ready(reference_frames)
    capture_start = first_ready(capture_frames)
    reference = measure(reference_frames, reference_start)
    capture = measure(capture_frames, capture_start)
    report = {'reference_ready_frame': reference_start, 'capture_ready_frame': capture_start,
              'reference_ready_seconds': reference_start / 60, 'capture_ready_seconds': capture_start / 60,
              'frames_checked': len(reference), 'reference': reference, 'capture': capture}
    errors = {key: [abs(a[key]-b[key]) for a, b in zip(reference, capture)]
              for key in ('scale', 'angle', 'x', 'y')}
    report['maximum_errors'] = {key: max(values) for key, values in errors.items()}
    report['mean_errors'] = {key: float(np.mean(values)) for key, values in errors.items()}
    aligned = []
    for index, pose in enumerate(capture):
        candidates = range(max(0, index-1), min(len(reference), index+2))
        match = min(candidates, key=lambda i: abs(reference[i]['scale']-pose['scale']) +
                    abs(reference[i]['angle']-pose['angle']) / 30)
        aligned.append({'reference_frame': match, **{key: abs(reference[match][key]-pose[key]) for key in errors}})
    report['capture_phase_tolerance_frames'] = 1
    report['phase_adjusted_maximum_errors'] = {key: max(row[key] for row in aligned) for key in errors}
    report['matched_reference_frames'] = [row['reference_frame'] for row in aligned]
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({key: value for key, value in report.items() if key not in
                      ('reference', 'capture', 'matched_reference_frames')}, indent=2))
    adjusted = report['phase_adjusted_maximum_errors']
    if adjusted['scale'] > 0.015 or adjusted['angle'] > 0.7 or adjusted['x'] > 2 or adjusted['y'] > 2:
        raise SystemExit('Meter motion differs from GH3 beyond the one-frame capture tolerance')


if __name__ == '__main__':
    main()
