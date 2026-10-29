import argparse
import json
import subprocess
import sys
from pathlib import Path


def samples_for_shard(total, per_identity, shard_index, shard_count):
    total_identities = (total + per_identity - 1) // per_identity
    assigned = 0
    for identity in range(shard_index, total_identities, shard_count):
        assigned += min(per_identity, total - identity * per_identity)
    return assigned


def main():
    here = Path(__file__).resolve().parent
    project = here.parent
    parser = argparse.ArgumentParser(
        description="Run deterministic GradPupil Unity player shards in parallel."
    )
    parser.add_argument("--exe", type=Path, default=project / "build-windows" / "UnityEyes2.exe")
    parser.add_argument("--config", type=Path, default=here / "gradpupil_autoload.json")
    parser.add_argument("--output", type=Path, default=here / "out_unityeyes_v3_native46" / "imgs")
    parser.add_argument("--shards", type=int, default=2)
    parser.add_argument("--samples", type=int, default=None,
                        help="Global sample count; defaults to num_samples in the config")
    parser.add_argument("--seed", type=int, default=None,
                        help="Base seed; defaults to dataset.seed in the config")
    args = parser.parse_args()

    exe = args.exe.resolve()
    config_path = args.config.resolve()
    output = args.output.resolve()
    if not exe.is_file():
        raise FileNotFoundError(f"Unity player not found: {exe}")
    if not config_path.is_file():
        raise FileNotFoundError(f"Config not found: {config_path}")
    if args.shards < 1:
        raise ValueError("--shards must be at least 1")

    config = json.loads(config_path.read_text(encoding="utf-8-sig"))
    total = args.samples if args.samples is not None else int(config["num_samples"])
    dataset = config["dataset"]
    seed = args.seed if args.seed is not None else int(dataset.get("seed", 1729))
    per_identity = int(dataset["poses_per_identity"]) * len(dataset["pupil_diameters_mm"])
    if total < 1 or seed < 0 or per_identity < 1:
        raise ValueError("Invalid samples, seed, or grouped dataset dimensions")

    output.mkdir(parents=True, exist_ok=True)
    processes = []
    for shard in range(args.shards):
        shard_dir = output / f"shard_{shard:02d}"
        shard_dir.mkdir(parents=True, exist_ok=True)
        log_path = shard_dir / "unity.log"
        command = [
            str(exe),
            "-batchmode",
            "-logFile", str(log_path),
            "--config", str(config_path),
            "--output", str(output),
            "--samples", str(total),
            "--seed", str(seed),
            "--shard-index", str(shard),
            "--shard-count", str(args.shards),
            "--quit-on-complete",
        ]
        expected = samples_for_shard(total, per_identity, shard, args.shards)
        if expected == 0:
            print(f"skip shard {shard}/{args.shards}: expected=0", flush=True)
            continue
        print(f"launch shard {shard}/{args.shards}: expected={expected} log={log_path}", flush=True)
        processes.append((shard, expected, log_path, subprocess.Popen(command)))

    failed = False
    for shard, expected, log_path, process in processes:
        code = process.wait()
        shard_dir = output / f"shard_{shard:02d}"
        actual = len(list(shard_dir.glob("*.json")))
        ok = code == 0 and actual == expected
        print(
            f"shard {shard}: exit={code} json={actual}/{expected} "
            f"status={'ok' if ok else 'FAILED'} log={log_path}",
            flush=True,
        )
        failed |= not ok

    if failed:
        raise SystemExit(1)
    print(f"all {total} samples completed across {args.shards} shards in {output}")


if __name__ == "__main__":
    main()
