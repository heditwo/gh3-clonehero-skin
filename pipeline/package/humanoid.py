"""Find the humanoid in any skeleton (docs/PACKAGE-FORMAT.md section 9.6, docs/BLENDER-ADDON.md section 1.17).

The roles of character.json skeleton.humanoid come from the hierarchy and the bone positions: the mirror plane of the
body, the limb pairs on it, the legs as the pair that reaches the floor, the arms as the pair that hangs from the trunk
below the head, the elbow and the knee where a limb chain splits into two parts of near-equal length, twist bones as
joints on the straight line of a limb part, fingers by their order across the palm. Bone names are only a tie-breaker
and a check (skeleton.auto_map). The result has a confidence for each role, the frame of the model (up, front, left,
floor, height), and notes that say what to check.

numpy only and relative imports, so the Blender add-on bundles it (blender/build.py). The input positions can be in
any right-handed frame (Blender, glTF; not the runtime frame, which has z negated) and unit; the frame of the result is in
the same coordinates.
"""

import re
import unicodedata

import numpy as np

from . import skeleton

_HELPER = re.compile(
    r"(^|[^a-z])(ik|ctrl|control|helper|dummy|target|pole|mch|wgt|ref|nub|end|tip)([^a-z]|$)|ik$|ＩＫ|操作|先$|_end$|end$"
)
_ARM_WORDS = re.compile(r"arm|elbow|hand|wrist|shoulder|clavicle|collar|shldr|腕|ひじ|肘|手|肩")
_LEG_WORDS = re.compile(r"leg|thigh|knee|calf|shin|foot|ankle|toe|足|ひざ|膝")
_FINGERS = ("Index", "Middle", "Ring", "Little")
# 3ds Max Biped numbers the fingers from the thumb: Finger0, Finger01, Finger02.
_THUMB = re.compile(r"thumb|親指|finger0\d?$")
_AXES = np.array([[1.0, 0, 0], [0, 1.0, 0], [0, 0, 1.0]])


def _unit(v):
    v = np.asarray(v, dtype=np.float64)
    length = np.linalg.norm(v)
    return v / length if length > 1e-12 else v * 0


def _snap(v, degrees=20):
    """The nearest axis of the input frame when it is within the given angle (models are almost always on the axes)."""
    v = _unit(v)
    k = int(np.argmax(np.abs(v)))
    axis = _AXES[k] * np.sign(v[k])
    return axis if float(np.dot(axis, v)) > np.cos(np.radians(degrees)) else v


def _normalized(name):
    return unicodedata.normalize("NFKC", name).lower()


def _segment_distance(p, a, b):
    ab = b - a
    t = np.clip(np.dot(p - a, ab) / max(np.dot(ab, ab), 1e-12), 0, 1)
    return float(np.linalg.norm(p - (a + ab * t)))


class Skeleton:
    """The bones of a model: names, parent indices, head positions, optional tail positions and weighted vertex counts."""

    def __init__(self, names, parents, heads, tails=None, weights=None):
        self.names = list(names)
        self.parents = [int(p) for p in parents]
        self.count = len(self.names)
        self.heads = np.asarray(heads, dtype=np.float64).reshape(-1, 3)
        self.tails = None if tails is None else np.asarray(tails, dtype=np.float64).reshape(-1, 3)
        self.children = [[] for _ in range(self.count)]
        for i, p in enumerate(self.parents):
            if p >= 0:
                self.children[p].append(i)
        order, stack = [], [i for i in range(self.count) if self.parents[i] < 0]
        while stack:
            bone = stack.pop()
            order.append(bone)
            stack.extend(self.children[bone])
        if len(order) != self.count:
            raise ValueError("the skeleton has a cycle")
        self.order = order
        helper = np.array([bool(_HELPER.search(_normalized(n))) for n in self.names])
        if weights is None or not np.any(np.asarray(weights) > 0):
            weighted = ~helper
        else:
            weighted = np.asarray(weights) > 0
        relevant = weighted.copy()
        for bone in reversed(order):
            if relevant[bone] and self.parents[bone] >= 0:
                relevant[self.parents[bone]] = True
        self.weighted, self.relevant = weighted, relevant
        self.kids = [[c for c in self.children[i] if relevant[c]] for i in range(self.count)]
        self.points = self.heads[relevant] if np.any(relevant) else self.heads
        extent = self.points.max(0) - self.points.min(0) if len(self.points) else np.ones(3)
        self.size = float(max(np.max(extent), 1e-6))
        self.depth = [0] * self.count
        for bone in order:
            if self.parents[bone] >= 0:
                self.depth[bone] = self.depth[self.parents[bone]] + 1
        self.subtree = [[] for _ in range(self.count)]
        for bone in reversed(order):
            if not relevant[bone]:
                continue
            self.subtree[bone] = [bone] + [d for c in self.kids[bone] for d in self.subtree[c]]

    def end(self, bone):
        """The far point of a bone: its tail (when it is plausible: importers guess the tails), else the head of its
        relevant child, else its head."""
        if self.tails is not None and np.linalg.norm(self.tails[bone] - self.heads[bone]) < 0.25 * self.size:
            return self.tails[bone]
        if self.kids[bone]:
            return self.heads[self.kids[bone][0]]
        return self.heads[bone]

    def above(self, a, b):
        p = self.parents[b]
        while p >= 0:
            if p == a:
                return True
            p = self.parents[p]
        return False

    def lca(self, a, b):
        chain = set()
        x = a
        while x >= 0:
            chain.add(x)
            x = self.parents[x]
        x = b
        while x >= 0 and x not in chain:
            x = self.parents[x]
        return x

    def path_down(self, top, bottom):
        """The bones from top (exclusive) down to bottom (inclusive), or [] when top is not above bottom."""
        out, x = [], bottom
        while x >= 0 and x != top:
            out.append(x)
            x = self.parents[x]
        return list(reversed(out)) if x == top else []

    def reach(self, bone):
        points = self.heads[self.subtree[bone]] if self.subtree[bone] else self.heads[[bone]]
        return float(np.max(np.linalg.norm(points - self.heads[bone], axis=1)))


def _mirror_plane(sk):
    """The mirror plane of the body (normal, point) from pairs of sibling subtrees that mirror each other."""
    candidates = []
    for node in range(sk.count):
        kids = sk.kids[node]
        if len(kids) < 2 or len(kids) > 24:
            continue
        for i in range(len(kids)):
            for j in range(i + 1, len(kids)):
                a, b = kids[i], kids[j]
                d = sk.heads[a] - sk.heads[b]
                if np.linalg.norm(d) < 0.01 * sk.size:
                    continue
                na, nb = len(sk.subtree[a]), len(sk.subtree[b])
                if abs(na - nb) > max(1, 0.25 * max(na, nb)):
                    continue
                normal, point = _unit(d), (sk.heads[a] + sk.heads[b]) / 2
                error = _mirror_error(sk, a, b, normal, point)
                extent = max(sk.reach(a), 0.02 * sk.size)
                if error > 0.12 * extent + 0.01 * sk.size:
                    continue
                candidates.append((normal, point, min(na, nb) * extent))
    if not candidates:
        return None, None
    best, best_mass, best_group = None, -1.0, []
    for normal, point, _ in candidates:
        group = [
            (n2 * np.sign(np.dot(n2, normal)), p2, m2)
            for n2, p2, m2 in candidates
            if abs(np.dot(n2, normal)) > 0.95 and abs(np.dot(p2 - point, normal)) < 0.05 * sk.size
        ]
        mass = sum(m for _, _, m in group)
        if mass > best_mass:
            best_mass, best_group = mass, group
    normal = _snap(_unit(np.sum([n2 * m for n2, _, m in best_group], axis=0)), 8)
    # The plane offset that mirrors the whole skeleton best (side-by-side hair strands pass the pair test too).
    points = sk.heads[sk.relevant]
    offsets = sorted(
        {round(float(np.dot(p2, normal)), 5) for _, p2, _ in best_group} | {float(np.median(points @ normal))}
    )

    def fit(offset):
        reflected = points - 2 * np.outer(points @ normal - offset, normal)
        return float(np.median(np.min(np.linalg.norm(reflected[:, None, :] - points[None, :, :], axis=2), axis=1)))

    offset = min(offsets, key=fit)
    return normal, normal * offset


def _mirror_error(sk, a, b, normal, point):
    pa = sk.heads[sk.subtree[a][:48]]
    pb = sk.heads[sk.subtree[b][:48]]
    reflected = pa - 2 * np.outer((pa - point) @ normal, normal)
    distance = np.linalg.norm(reflected[:, None, :] - pb[None, :, :], axis=2)
    return float(np.mean(distance.min(1)))


def _partners(sk, normal, point):
    """The mirror partner of each relevant bone (itself for a bone on the mirror plane), -1 when none. Pairs are
    mutual: the closest pairs (position, then depth and subtree size) are taken first."""
    partner = np.full(sk.count, -1)
    idx = np.flatnonzero(sk.relevant)
    if not len(idx):
        return partner
    heads = sk.heads[idx]
    reflected = heads - 2 * np.outer((heads - point) @ normal, normal)
    cost = np.linalg.norm(reflected[:, None, :] - heads[None, :, :], axis=2)
    depth = np.array([sk.depth[i] for i in idx])
    sizes = np.array([len(sk.subtree[i]) for i in idx])
    cost = (
        cost
        + np.abs(depth[:, None] - depth[None, :]) * 0.01 * sk.size
        + np.abs(sizes[:, None] - sizes[None, :]) * 0.004 * sk.size
    )
    # A bone is its own partner (on the mirror plane) only when it is very near the plane.
    side = np.abs((heads - point) @ normal)
    np.fill_diagonal(cost, np.where(side < 0.008 * sk.size, np.diag(cost), np.inf))
    limit = 0.03 * sk.size
    rows, cols = np.nonzero(cost < limit)
    order = np.argsort(cost[rows, cols], kind="stable")
    for k in order:
        a, b = idx[rows[k]], idx[cols[k]]
        if partner[a] >= 0 or partner[b] >= 0:
            continue
        partner[a], partner[b] = b, a
    return partner


def _match_children(sk, x, y, plane):
    """The mirrored pairs among the children of x and of y (by position, then by the size of their subtrees)."""
    normal, point = plane
    pairs = []
    for c in sk.kids[x]:
        reflected = sk.heads[c] - 2 * np.dot(sk.heads[c] - point, normal) * normal
        best, best_cost = -1, 0.03 * sk.size
        for d in sk.kids[y]:
            cost = (
                np.linalg.norm(sk.heads[d] - reflected) + abs(len(sk.subtree[c]) - len(sk.subtree[d])) * 0.004 * sk.size
            )
            if cost < best_cost:
                best, best_cost = d, cost
        if best >= 0:
            pairs.append((c, best))
    return pairs


def _pair_chains(sk, a, b, partner):
    """The limb chains from a mirrored pair of roots, joint by joint while both sides go on the same way: a child with
    children wins over a leaf (twist and roll helpers are leaves), and the chain stops where two or more children
    with children start (fingers). Props on one side only (a bow, a sword) have no partner and do not count."""
    left, right = [a], [b]
    x, y = a, b
    while len(left) < 64:
        pairs = _match_children(sk, x, y, partner)
        if not pairs:
            break
        inner = [(c, d) for c, d in pairs if sk.kids[c] and sk.kids[d]]
        options = inner or pairs
        reach = sorted(
            ((sk.reach(c) + float(np.linalg.norm(sk.heads[c] - sk.heads[x])), c, d) for c, d in options), reverse=True
        )
        if len(reach) > 1 and reach[0][0] < 2.5 * reach[1][0]:
            break
        if not inner and len(pairs) > 1:
            break
        _, x, y = reach[0]
        left.append(x)
        right.append(y)
    return left, right


def _chain_length(sk, chain):
    points = [sk.heads[b] for b in chain] + [sk.end(chain[-1])]
    return float(sum(np.linalg.norm(points[i + 1] - points[i]) for i in range(len(points) - 1)))


def _straight(sk, chain, i, j, tolerance):
    a, b = sk.heads[chain[i]], sk.heads[chain[j]]
    length = np.linalg.norm(b - a)
    return all(_segment_distance(sk.heads[chain[k]], a, b) <= tolerance * length for k in range(i + 1, j))


def _limb_split(
    sk,
    chain,
    hint_roles,
    upper_role,
    lower_role,
    end_role,
    ratio_mid,
    height=None,
    floor=None,
    up=None,
    end_low=False,
    lateral=None,
):
    """The (upper, middle, end) joints of a limb chain: two straight parts of near-equal length. Returns
    (indices into chain, quality 0-1) or None."""
    best, best_score = None, -1e9
    n = len(chain)
    # Of joints at one place (MMD cancel and shoulder C bones), the deeper one with weights has the role.
    same = [
        i + 1 < n and np.linalg.norm(sk.heads[chain[i + 1]] - sk.heads[chain[i]]) < 0.005 * sk.size for i in range(n)
    ]
    for i in range(n):
        if same[i]:
            continue
        for j in range(i + 1, n):
            if same[j]:
                continue
            for k in range(j + 1, n):
                if same[k]:
                    continue
                lu = np.linalg.norm(sk.heads[chain[j]] - sk.heads[chain[i]])
                ll = np.linalg.norm(sk.heads[chain[k]] - sk.heads[chain[j]])
                if lu < 0.02 * sk.size or ll < 0.02 * sk.size:
                    continue
                ratio = lu / ll
                if not 0.5 < ratio < 2.2:
                    continue
                if not (_straight(sk, chain, i, j, 0.05) and _straight(sk, chain, j, k, 0.05)):
                    continue
                score = -abs(np.log(ratio / ratio_mid)) * 2 + (lu + ll) / sk.size
                # The upper joint of a limb is away from the middle of the body (a collarbone starts near it).
                if lateral is not None:
                    score -= max(0.0, 0.06 - lateral(chain[i])) * 30
                if end_low:
                    rel = (np.dot(sk.heads[chain[k]], up) - floor) / height
                    score -= max(0.0, rel - 0.12) * 8
                    # The thigh and the shin go down at rest; the foot goes forward.
                    for x, y, least in ((i, j, 0.75), (j, k, 0.85)):
                        down = np.dot(_unit(sk.heads[chain[y]] - sk.heads[chain[x]]), -up)
                        score -= max(0.0, least - down) * 6
                    after = chain[k + 1 :]
                    if after:
                        step = sk.heads[after[0]] - sk.heads[chain[k]]
                        flat = np.linalg.norm(step - up * np.dot(step, up))
                        if flat < abs(np.dot(step, up)) * 0.6:
                            score -= 1.0
                else:
                    rest = _chain_length(sk, chain[k:]) if k < n - 1 else 0.0
                    hand = max([sk.reach(chain[k])] + [rest])
                    if hand > 0.9 * ll:
                        score -= (hand / ll - 0.9) * 3
                for role, at in ((upper_role, i), (lower_role, j), (end_role, k)):
                    if hint_roles.get(chain[at]) == role:
                        score += 0.4
                if score > best_score:
                    best, best_score = (i, j, k), score
    if best is None:
        return None
    i, j, k = best
    lu = np.linalg.norm(sk.heads[chain[j]] - sk.heads[chain[i]])
    ll = np.linalg.norm(sk.heads[chain[k]] - sk.heads[chain[j]])
    quality = float(np.clip(1.2 - abs(np.log(lu / ll / ratio_mid)), 0.4, 1.0))
    return best, quality


def _fingers(sk, hand, other, plane, hint_roles, front=None):
    """Finger chains below a hand joint: (role prefix 'Thumb'/'Index'/..., joints) and a quality. Only children with a
    mirrored partner below the other hand count (a bow or a sword in one hand is not a finger)."""
    paths = []

    def walk(bone, path):
        if not sk.kids[bone]:
            paths.append(path)
            return
        for c in sk.kids[bone]:
            walk(c, path + [c])

    mirrored = {c for c, _ in _match_children(sk, hand, other, plane)} if other >= 0 else set(sk.kids[hand])
    for c in sk.kids[hand]:
        if c in mirrored:
            walk(c, [c])
    if len(paths) < 2:
        return [], 1.0
    counts = {}
    for path in paths:
        for bone in path:
            counts[bone] = counts.get(bone, 0) + 1
    chains = []
    for path in paths:
        own = [b for b in path if counts[b] == 1]
        if own:
            chains.append(own[:4])
    if len(chains) < 2:
        return [], 1.0
    wrist = sk.heads[hand]
    bases = np.array([sk.heads[c[0]] for c in chains])
    tips = np.array([sk.heads[c[-1]] if len(c) > 1 else sk.end(c[0]) for c in chains])
    axis = _unit(np.mean(tips, 0) - wrist)
    deviation = []
    for c, base, tip in zip(chains, bases, tips):
        direction = _unit(tip - base) if np.linalg.norm(tip - base) > 1e-6 else axis
        along = np.dot(base - wrist, axis)
        hinted = any(
            hint_roles.get(b, "").endswith(("ThumbMetacarpal", "ThumbProximal", "ThumbDistal"))
            or _THUMB.search(_normalized(sk.names[b]))
            for b in c
        )
        deviation.append(
            np.degrees(np.arccos(np.clip(np.dot(direction, axis), -1, 1)))
            - along / max(sk.size, 1e-6) * 200
            + (25 if hinted else 0)
        )
    order = np.argsort(deviation)[::-1]
    margin = deviation[order[0]] - deviation[order[1]]
    # A hand has a thumb when one finger leaves the hand at another angle or nearer the wrist (or its name says so).
    thumb = int(order[0]) if margin > 12 else -1
    quality = float(np.clip(margin / 25, 0.3, 1.0)) if len(chains) > 2 else 0.5
    others = [i for i in range(len(chains)) if i != thumb]
    if thumb >= 0:
        side = bases[thumb] - wrist
    else:
        # No thumb: the index finger is the one at the front of the hand (the thumb side in a T-pose or an A-pose).
        quality = 0.6
        side = np.asarray(front, dtype=np.float64) if front is not None else bases[others[0]] - bases[others[-1]]
    side = _unit(side - axis * np.dot(side, axis))
    others.sort(key=lambda i: -np.dot(bases[i] - wrist, side))
    names = {1: ("Middle",), 2: ("Index", "Middle"), 3: ("Index", "Middle", "Ring"), 4: _FINGERS}.get(
        len(others), _FINGERS
    )
    if len(others) > 4:
        others = others[:2] + others[-2:]
    knuckle = np.median([np.linalg.norm(sk.heads[chains[i][1 if len(chains[i]) > 1 else 0]] - wrist) for i in others])
    result = []
    for name, i in zip(names, others):
        joints = chains[i]
        if len(joints) >= 4 or (len(joints) >= 2 and np.linalg.norm(sk.heads[joints[0]] - wrist) < 0.45 * knuckle):
            joints = joints[1:]
        result.append((name, joints[:3]))
    if thumb >= 0:
        result.append(("Thumb", chains[thumb][:3]))
    return result, quality


def _finger_roles(side, finger, joints, sk, wrist_knuckle):
    if finger == "Thumb":
        parts = (
            skeleton.THUMB_PARTS
            if len(joints) >= 3
            else ("Metacarpal", "Proximal")
            if len(joints) == 2
            else ("Proximal",)
        )
    else:
        parts = skeleton.FINGER_PARTS[: len(joints)]
    return {side + finger + part: bone for part, bone in zip(parts, joints)}


def find(names, parents, heads, tails=None, weights=None, points=None):
    """The humanoid map of a skeleton. names, parents (indices, -1 for a root), heads (bone positions); tails: the
    tail of each bone (Blender), else the next joint gives the direction; weights: weighted vertex count of each bone
    (bones without weights and without weighted children are helpers); points: mesh vertex positions (optional, for
    the height and the front).

    Returns a dict: roles {role: bone index}, confidence {role: 0-1}, frame {up, forward, left, floor, height,
    mesh_height}, mirrored (True when the bone names say left where the body is right), notes [text], and found
    (False when no humanoid was found)."""
    sk = Skeleton(names, parents, heads, tails, weights)
    notes = []
    result = {"roles": {}, "confidence": {}, "frame": None, "mirrored": False, "notes": notes, "found": False}
    hint_map = skeleton.auto_map(names, parents, deform=[n for n, w in zip(names, sk.weighted) if w])
    index = {n: i for i, n in enumerate(names)}
    hint_roles = {index[b]: r for r, b in hint_map.items() if b in index}
    normal, point = _mirror_plane(sk)
    if normal is None:
        notes.append("no left and right body parts were found: the skeleton has no mirrored limbs")
        return result
    partner = _partners(sk, normal, point)
    roots = []
    for bone in range(sk.count):
        if not sk.relevant[bone] or partner[bone] < 0 or partner[bone] == bone:
            continue
        parent = sk.parents[bone]
        if parent >= 0 and partner[parent] != parent and partner[parent] >= 0:
            continue
        if np.dot(sk.heads[bone] - point, normal) > 0:
            roots.append((bone, int(partner[bone])))
    limbs = []
    for a, b in roots:
        ca, cb = _pair_chains(sk, a, b, (normal, point))
        length = (_chain_length(sk, ca) + _chain_length(sk, cb)) / 2
        if min(len(ca), len(cb)) < 2 or length < 0.12 * sk.size:
            continue
        limbs.append({"roots": (a, b), "chains": (ca, cb), "length": length, "lca": sk.lca(a, b)})
    if len(limbs) < 2:
        notes.append("fewer than two limb pairs (legs and arms) were found")
        return result
    midline = [i for i in range(sk.count) if sk.relevant[i] and partner[i] == i]
    hypotheses = []
    for legs in limbs:
        for arms in limbs:
            if legs is arms:
                continue
            if any(sk.above(x, legs["lca"]) or x == legs["lca"] for c in arms["chains"] for x in c):
                continue
            if any(sk.above(x, arms["lca"]) or x == arms["lca"] for c in legs["chains"] for x in c):
                continue
            attach_l = (sk.heads[legs["roots"][0]] + sk.heads[legs["roots"][1]]) / 2
            attach_a = (sk.heads[arms["roots"][0]] + sk.heads[arms["roots"][1]]) / 2
            up = attach_a - attach_l
            up = up - normal * np.dot(up, normal)
            if np.linalg.norm(up) < 0.02 * sk.size:
                continue
            up = _unit(up)
            h = sk.points @ up
            floor, top = float(h.min()), float(h.max())
            height = max(top - floor, 1e-6)
            score = 0.0
            ends = [min(np.dot(sk.heads[c[-1]], up), np.dot(sk.end(c[-1]), up)) for c in legs["chains"]]
            score += 2 * (1 - np.clip((max(ends) - floor) / height / 0.15, 0, 1))
            downs = []
            for c in legs["chains"]:
                for x, y in zip(c, c[1:]):
                    step = sk.heads[y] - sk.heads[x]
                    if np.linalg.norm(step) > 1e-6:
                        downs.append(np.dot(_unit(step), -up) * np.linalg.norm(step))
            score += float(np.clip(np.sum(downs) / max(legs["length"] * 2, 1e-6), -1, 1)) * 1.5
            rel_a, rel_l = (np.dot(attach_a, up) - floor) / height, (np.dot(attach_l, up) - floor) / height
            score += 1 if 0.45 <= rel_a <= 0.97 else -1
            score += 1 if 0.12 <= rel_l <= 0.7 else -1
            ratio = arms["length"] / max(legs["length"], 1e-6)
            score += 1 if 0.35 <= ratio <= 1.35 else -1
            mid_top = max(midline, key=lambda i: np.dot(sk.heads[i], up), default=-1)
            if mid_top >= 0 and (arms["lca"] == mid_top or sk.above(arms["lca"], mid_top)):
                score += 1.5
            else:
                score -= 1.5
            if mid_top >= 0 and np.dot(sk.heads[mid_top], up) > np.dot(attach_a, up) + 0.03 * height:
                score += 0.5
            if any(len(sk.kids[c[-1]]) >= 2 for c in arms["chains"]):
                score += 0.5
            text = " ".join(_normalized(names[x]) for c in legs["chains"] for x in c)
            if _LEG_WORDS.search(text):
                score += 0.5
            text = " ".join(_normalized(names[x]) for c in arms["chains"] for x in c)
            if _ARM_WORDS.search(text):
                score += 0.5
            hinted = sum(
                1 for c in legs["chains"] for x in c if hint_roles.get(x, "").endswith(("UpperLeg", "LowerLeg", "Foot"))
            )
            hinted += sum(
                1 for c in arms["chains"] for x in c if hint_roles.get(x, "").endswith(("UpperArm", "LowerArm", "Hand"))
            )
            score += min(hinted, 6) * 0.1
            hypotheses.append((score, id(legs), id(arms), legs, arms, up, floor, height))
    if not hypotheses:
        notes.append("no pair of legs and arms was found")
        return result
    hypotheses.sort(key=lambda h: -h[0])
    result["debug"] = [
        (round(float(h[0]), 2), names[h[3]["roots"][0]], names[h[4]["roots"][0]]) for h in hypotheses[:6]
    ]
    score, _, _, legs, arms, up, floor, height = hypotheses[0]
    margin = score - hypotheses[1][0] if len(hypotheses) > 1 else 3.0
    structure = float(np.clip(0.55 + margin / 4, 0.4, 1.0)) if score > 3 else 0.35
    up = _snap(up)
    normal = _snap(normal - up * np.dot(normal, up), 10)
    h = sk.points @ up
    floor, height = float(h.min()), float(h.max() - h.min())
    roles, confidence = {}, {}

    def put(role, bone, c):
        if bone is None or bone < 0:
            return
        roles[role] = int(bone)
        confidence[role] = float(np.clip(c, 0, 1))

    # Legs: hip, knee, ankle; the toes are the next joint that goes forward.
    leg_rows = []
    for chain in legs["chains"]:
        split = _limb_split(
            sk,
            chain,
            hint_roles,
            "UpperLeg",
            "LowerLeg",
            "Foot",
            1.1,
            height,
            floor,
            up,
            end_low=True,
            lateral=lambda bone: 0.06 + abs(np.dot(sk.heads[bone] - point, normal)) / height,
        )
        if split is None:
            notes.append(f"the leg chain from '{names[chain[0]]}' has no knee: check the leg roles")
            leg_rows.append(None)
            continue
        (i, j, k), q = split
        toes = None
        after = chain[k + 1 :]
        if not after and sk.kids[chain[k]]:
            # The foot ends where two children start (heel and toes): the toes are the child farthest out.
            flat = lambda x: np.linalg.norm(
                (sk.heads[x] - sk.heads[chain[k]]) - up * np.dot(sk.heads[x] - sk.heads[chain[k]], up)
            )
            best = max(sk.kids[chain[k]], key=flat)
            if flat(best) > 0.01 * sk.size:
                after = [best]
        if after:
            flat = [
                np.linalg.norm((sk.heads[x] - sk.heads[chain[k]]) - up * np.dot(sk.heads[x] - sk.heads[chain[k]], up))
                for x in after
            ]
            far = max(
                flat
                + [
                    np.linalg.norm(
                        (sk.end(after[-1]) - sk.heads[chain[k]])
                        - up * np.dot(sk.end(after[-1]) - sk.heads[chain[k]], up)
                    )
                ]
            )
            toes = next((x for x, f in zip(after, flat) if f >= 0.3 * far and f > 0.01 * sk.size), None)
        leg_rows.append((chain, i, j, k, toes, q))
    # The front: the toes and the mesh at the floor point forward, the thumbs and the eyes too.
    forward_axis = _unit(np.cross(up, normal))
    votes = 0.0
    for row in leg_rows:
        if row is None:
            continue
        chain, i, j, k, toes, q = row
        ankle = sk.heads[chain[k]]
        if toes is not None:
            votes += 2 * np.dot(_unit(sk.heads[toes] - ankle), forward_axis)
        elif sk.tails is not None:
            votes += np.dot(_unit(sk.end(chain[k]) - ankle), forward_axis)
    if points is not None and len(points):
        pts = np.asarray(points, dtype=np.float64)
        ph = pts @ up
        low = pts[ph < ph.min() + 0.05 * max(np.ptp(ph), 1e-6)]
        ankles = [sk.heads[r[0][r[3]]] for r in leg_rows if r is not None]
        if len(low) and ankles:
            offset = np.dot(np.mean(low, 0) - np.mean(ankles, 0), forward_axis)
            votes += 3 * np.clip(offset / (0.03 * max(height, 1e-6)), -1, 1)
    arm_rows = []
    for chain in arms["chains"]:
        split = _limb_split(
            sk,
            chain,
            hint_roles,
            "UpperArm",
            "LowerArm",
            "Hand",
            1.05,
            lateral=lambda bone: abs(np.dot(sk.heads[bone] - point, normal)) / height,
        )
        if split is None:
            notes.append(f"the arm chain from '{names[chain[0]]}' has no elbow: check the arm roles")
            arm_rows.append(None)
            continue
        arm_rows.append((chain, *split[0], split[1]))
    for row in arm_rows:
        if row is None:
            continue
        chain, i, j, k, q = row
        other = (
            arm_rows[1 - arm_rows.index(row)] if len(arm_rows) == 2 and all(r is not None for r in arm_rows) else None
        )
        fingers, _ = _fingers(sk, chain[k], other[0][other[3]] if other else -1, (normal, point), hint_roles)
        thumb = next((f for f in fingers if f[0] == "Thumb"), None)
        rest = [sk.heads[f[1][0]] for f in fingers if f[0] != "Thumb"]
        if thumb is not None and rest:
            votes += 1.5 * np.clip(
                np.dot(sk.heads[thumb[1][0]] - np.mean(rest, 0), forward_axis) / (0.01 * sk.size), -1, 1
            )
    forward = forward_axis if votes >= 0 else -forward_axis
    if abs(votes) < 0.5:
        notes.append(
            "the front of the model is not clear (no toes, no feet in the mesh): check that the model looks toward -Y"
        )
    forward = _snap(forward)
    left = _unit(np.cross(up, forward))
    side_of = lambda bone: "left" if np.dot(sk.heads[bone] - point, left) > 0 else "right"
    mismatch, match = 0, 0
    for row in leg_rows:
        if row is None:
            continue
        chain, i, j, k, toes, q = row
        side = side_of(chain[i])
        hinted = hint_roles.get(chain[i], "")
        if hinted.startswith(("left", "right")):
            match, mismatch = (match + 1, mismatch) if hinted.startswith(side) else (match, mismatch + 1)
        base = structure * q
        put(side + "UpperLeg", chain[i], base)
        put(side + "LowerLeg", chain[j], base)
        put(side + "Foot", chain[k], base)
        if toes is not None:
            put(side + "Toes", toes, base * 0.9)
    # Arms: shoulder (the joint before the upper arm), twist joints on the straight parts, the hand, the fingers.
    for row in arm_rows:
        if row is None:
            continue
        chain, i, j, k, q = row
        side = side_of(chain[i])
        hinted = hint_roles.get(chain[i], "")
        if hinted.startswith(("left", "right")):
            match, mismatch = (match + 1, mismatch) if hinted.startswith(side) else (match, mismatch + 1)
        base = structure * q
        if i > 0:
            shoulder = chain[i - 1]
            if np.linalg.norm(sk.heads[shoulder] - sk.heads[chain[i]]) > 0.01 * sk.size or i == 1:
                put(side + "Shoulder", shoulder, base * 0.9)
            elif i > 1:
                put(side + "Shoulder", chain[i - 2], base * 0.8)
        put(side + "UpperArm", chain[i], base)
        put(side + "LowerArm", chain[j], base)
        put(side + "Hand", chain[k], base)

        # A twist joint lies inside a straight part, away from its ends.
        def inside(x, a, b):
            t = np.dot(sk.heads[x] - sk.heads[a], sk.heads[b] - sk.heads[a]) / max(
                np.dot(sk.heads[b] - sk.heads[a], sk.heads[b] - sk.heads[a]), 1e-12
            )
            return 0.1 < t < 0.9

        if j - i > 1 and sk.parents[chain[i + 1]] == chain[i] and inside(chain[i + 1], chain[i], chain[j]):
            put(side + "UpperArmTwist", chain[i + 1], base * 0.8)
        twists = [x for x in chain[j + 1 : k] if inside(x, chain[j], chain[k])]
        if twists:
            put(side + "LowerArmTwist", twists[0], base * 0.8)
        other = (
            arm_rows[1 - arm_rows.index(row)] if len(arm_rows) == 2 and all(r is not None for r in arm_rows) else None
        )
        fingers, fq = _fingers(sk, chain[k], other[0][other[3]] if other else -1, (normal, point), hint_roles, forward)
        knuckles = [np.linalg.norm(sk.heads[f[1][0]] - sk.heads[chain[k]]) for f in fingers if f[0] != "Thumb"]
        for finger, joints in fingers:
            for role, bone in _finger_roles(
                side, finger, joints, sk, float(np.median(knuckles)) if knuckles else 0.05 * sk.size
            ).items():
                put(role, bone, base * 0.85 * fq)
    match, mismatch = 0, 0
    for bone in range(sk.count):
        offset = np.dot(sk.heads[bone] - point, left)
        if not sk.relevant[bone] or abs(offset) < 0.02 * height:
            continue
        token = skeleton._split(names[bone])[0]
        if token:
            match, mismatch = (match + 1, mismatch) if (token == "l") == (offset > 0) else (match, mismatch + 1)
    if mismatch >= 4 and mismatch > 3 * match:
        result["mirrored"] = True
        notes.append(
            "the bone names say left where the body is right: the model is mirrored (a Unity or left-handed export)"
        )
    # Trunk: hips (where the legs meet), the spine up to where the arms meet, the neck, and the head.
    legs_at, arms_at = legs["lca"], arms["lca"]
    hips = legs_at
    if sk.above(legs_at, arms_at):
        parent = sk.parents[legs_at]
        if (
            parent >= 0
            and abs(np.dot(sk.heads[parent] - sk.heads[legs_at], up)) < 0.15 * height
            and partner[parent] == parent
        ):
            hips = parent
    put("hips", hips, structure)
    arm_roots = set(arms["roots"])
    upward, bone = [], arms_at
    while True:
        options = [c for c in sk.kids[bone] if partner[c] == c and c not in arm_roots]
        if not options:
            break
        best = max(options, key=lambda c: max(np.dot(sk.heads[x], up) for x in sk.subtree[c]))
        if max(np.dot(sk.heads[x], up) for x in sk.subtree[best]) < np.dot(sk.heads[bone], up) + 0.01 * height:
            break
        upward.append(best)
        bone = best
        risers = [
            c
            for c in sk.kids[best]
            if max(np.dot(sk.heads[x], up) for x in sk.subtree[c]) >= np.dot(sk.heads[best], up) + 0.01 * height
            or partner[c] != c
        ]
        if len(risers) >= 2:
            break
    head = upward[-1] if upward else -1
    hinted_head = next((b for b, r in hint_roles.items() if r == "head"), -1)
    if hinted_head >= 0 and hinted_head in upward:
        head = hinted_head
    neck_chain = upward[: upward.index(head)] if head in upward else []
    chest = arms_at
    shoulder_height = (
        min(
            np.dot(sk.heads[arm_rows[x][0][arm_rows[x][1]]], up)
            for x in range(len(arm_rows))
            if arm_rows[x] is not None
        )
        if any(r is not None for r in arm_rows)
        else np.dot(sk.heads[arms_at], up)
    )
    if (
        not neck_chain
        and head >= 0
        and np.dot(sk.heads[arms_at], up) >= shoulder_height - 0.02 * height
        and sk.parents[arms_at] >= 0
    ):
        neck_chain, chest = [arms_at], sk.parents[arms_at]
    base = sk.lca(hips, chest)
    spine = sk.path_down(base, chest)
    spine = [b for b in spine if b != hips]
    if head < 0:
        notes.append("no head was found above the arms: set the head role")
    put("head", head, structure * (0.9 if head == hinted_head or hinted_head < 0 else 0.6))
    if neck_chain:
        put("neck", neck_chain[0], structure * 0.9)
    if not spine:
        notes.append("no spine bone between the hips and the arms: set the spine role")
    elif len(spine) == 1:
        put("spine", spine[0], structure)
    elif len(spine) == 2:
        put("spine", spine[0], structure)
        put("chest", spine[1], structure)
    else:
        put("spine", spine[0], structure)
        put("chest", spine[len(spine) // 2] if len(spine) > 3 else spine[1], structure * 0.9)
        put("upperChest", spine[-1], structure)
    # Eyes: a mirrored pair of short joints in front of the head; the jaw only from its name.
    if head >= 0:
        best_eyes = None
        for a, b in roots:
            if not (sk.above(head, a) and sk.above(head, b)):
                continue
            if sk.reach(a) > 0.06 * height:
                continue
            if np.dot(sk.heads[a] - sk.heads[head], up) < -0.02 * height:
                continue
            hinted = hint_roles.get(a, "").endswith("Eye")
            front = np.dot(sk.heads[a] - sk.heads[head], forward) + (1 if hinted else 0)
            if hinted or "eye" in _normalized(names[a]) or "目" in names[a]:
                if best_eyes is None or front > best_eyes[0]:
                    best_eyes = (front, a, b)
        if best_eyes is not None:
            for bone in best_eyes[1:]:
                put(side_of(bone) + "Eye", bone, 0.7)
        jaw = next((b for b, r in hint_roles.items() if r == "jaw"), -1)
        if jaw >= 0 and sk.above(head, jaw):
            put("jaw", jaw, 0.7)
    # The bone names check the result: a role whose name says another role gets a low confidence and a note.
    for role, bone in list(roles.items()):
        named = hint_map.get(role)
        if named is None:
            continue
        if named == names[bone]:
            confidence[role] = max(confidence[role], 0.9)
        elif named in index and role in skeleton.REQUIRED and not result["mirrored"]:
            confidence[role] = min(confidence[role], 0.5)
            notes.append(
                f"{role}: the bone '{names[bone]}' has the place of this role, but the name of '{named}' says {role}: check it"
            )
    used = {}
    for role in sorted(roles, key=lambda r: -confidence[r]):
        if roles[role] in used:
            notes.append(f"'{names[roles[role]]}' was found for {used[roles[role]]} and {role}: check them")
            del roles[role]
            del confidence[role]
        else:
            used[roles[role]] = role
    mesh_height = None
    if points is not None and len(points):
        ph = np.asarray(points, dtype=np.float64) @ up
        mesh_height = float(ph.max() - ph.min())
        floor = float(ph.min())
    missing = [r for r in skeleton.REQUIRED if r not in roles]
    if missing:
        notes.append(f"not found: {', '.join(missing)}")
    result.update(
        roles=roles,
        confidence=confidence,
        found=not missing,
        frame={
            "up": up.tolist(),
            "forward": forward.tolist(),
            "left": left.tolist(),
            "floor": floor,
            "height": height,
            "mesh_height": mesh_height,
            "center": point.tolist(),
        },
    )
    return result


def helper_followers(names, parents, heads, weights, mapping, taken=()):
    """skeleton.followers for helper bones with weights that start where a mapped sibling bone starts (VRM aim and
    roll bones, clothing helpers): they turn with that bone. mapping: {role: bone name}; taken: bones that already
    follow another bone. Returns rows {bone, source, weight}."""
    heads = np.asarray(heads, dtype=np.float64)
    size = float(np.ptp(heads, axis=0).max()) if len(heads) else 1.0
    index = {n: i for i, n in enumerate(names)}
    mapped = {index[b] for b in mapping.values() if b in index}
    rows = []
    for bone in range(len(names)):
        if bone in mapped or names[bone] in taken or not weights[bone] > 0:
            continue
        twins = [
            m for m in mapped if parents[m] == parents[bone] and np.linalg.norm(heads[m] - heads[bone]) < 0.01 * size
        ]
        if twins:
            rows.append({"bone": names[bone], "source": names[twins[0]], "weight": 1.0})
    return rows
