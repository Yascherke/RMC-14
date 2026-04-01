from __future__ import annotations

import argparse
import csv
import json
import os
import re
import sys
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any

from sqlalchemy import bindparam, create_engine, text


LOG_TYPE_CHAT = 61
DEFAULT_ENV_FILE = ".env"


@dataclass(frozen=True)
class Pattern:
    chat_type: str
    regex: re.Pattern[str]


PATTERNS: tuple[Pattern, ...] = (
    Pattern("say", re.compile(r"^Say from (?P<sender>.+?) as (?P<alias>.+?): (?P<message>.+)\.$")),
    Pattern("say", re.compile(r"^Say from (?P<sender>.+?): (?P<message>.+)\.$")),
    Pattern(
        "say_transformed",
        re.compile(
            r"^Say from (?P<sender>.+?) as (?P<alias>.+?), original: (?P<original>.+), transformed: (?P<transformed>.+)\.$"
        ),
    ),
    Pattern(
        "say_transformed",
        re.compile(r"^Say from (?P<sender>.+?), original: (?P<original>.+), transformed: (?P<transformed>.+)\.$"),
    ),
    Pattern("whisper", re.compile(r"^Whisper from (?P<sender>.+?) as (?P<alias>.+?): (?P<message>.+)\.$")),
    Pattern("whisper", re.compile(r"^Whisper from (?P<sender>.+?): (?P<message>.+)\.$")),
    Pattern(
        "whisper_transformed",
        re.compile(
            r"^Whisper from (?P<sender>.+?) as (?P<alias>.+?), original: (?P<original>.+), transformed: (?P<transformed>.+)\.$"
        ),
    ),
    Pattern(
        "whisper_transformed",
        re.compile(r"^Whisper from (?P<sender>.+?), original: (?P<original>.+), transformed: (?P<transformed>.+)\.$"),
    ),
    Pattern("emote", re.compile(r"^Emote from (?P<sender>.+?) as (?P<alias>.+?): (?P<message>.+)$")),
    Pattern("emote", re.compile(r"^Emote from (?P<sender>.+?): (?P<message>.+)$")),
    Pattern("looc", re.compile(r"^LOOC from (?P<sender>.+?): (?P<message>.+)$")),
    Pattern("dead_chat", re.compile(r"^Dead chat from (?P<sender>.+?): (?P<message>.+)$")),
    Pattern("admin_dead_chat", re.compile(r"^Admin dead chat from (?P<sender>.+?): (?P<message>.+)$")),
    Pattern("ooc", re.compile(r"^OOC from (?P<sender>.+?): (?P<message>.+)$")),
    Pattern("admin_chat", re.compile(r"^Admin chat from (?P<sender>.+?): (?P<message>.+)$")),
    Pattern("mentor_chat", re.compile(r"^Mentor chat from (?P<sender>.+?): (?P<message>.+)$")),
    Pattern("hook_ooc", re.compile(r"^Hook OOC from (?P<sender>.+?): (?P<message>.+)$")),
    Pattern("hook_admin", re.compile(r"^Hook admin from (?P<sender>.+?): (?P<message>.+)$")),
    Pattern(
        "radio",
        re.compile(r"^Radio message from (?P<sender>.+?) as (?P<alias>.+?) on (?P<channel>.+?): (?P<message>.+)$"),
    ),
    Pattern(
        "radio",
        re.compile(r"^Radio message from (?P<sender>.+?) on (?P<channel>.+?): (?P<message>.+)$"),
    ),
    Pattern(
        "telephone",
        re.compile(r"^Telephone message from (?P<sender>.+?) as (?P<alias>.+?) on (?P<channel>.+?): (?P<message>.+)$"),
    ),
    Pattern(
        "telephone",
        re.compile(r"^Telephone message from (?P<sender>.+?) on (?P<channel>.+?): (?P<message>.+)$"),
    ),
    Pattern(
        "global_announcement",
        re.compile(r"^Global station announcement from (?P<sender>.+?): (?P<message>.+)$"),
    ),
    Pattern(
        "station_announcement",
        re.compile(r"^Station Announcement from (?P<sender>.+?): (?P<message>.+)$"),
    ),
    Pattern(
        "station_announcement",
        re.compile(r"^Station Announcement on (?P<channel>.+?) from (?P<sender>.+?): (?P<message>.+)$"),
    ),
    Pattern("server_announcement", re.compile(r"^Server announcement: (?P<message>.+)$")),
    Pattern("server_message", re.compile(r"^Server message to (?P<target>.+?): (?P<message>.+)$")),
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Extract normalized RMC14 chat logs from the server database.")
    parser.add_argument("--database-url", help="SQLAlchemy database URL.")
    parser.add_argument("--env-file", default=DEFAULT_ENV_FILE, help="Path to a .env file containing DATABASE_URL.")
    parser.add_argument("--round-id", type=int, action="append", help="Restrict output to one or more round IDs.")
    parser.add_argument("--last-rounds", type=int, help="Restrict output to the most recent N rounds.")
    parser.add_argument("--after", type=parse_datetime, help="Only include chat logs on or after this timestamp.")
    parser.add_argument("--before", type=parse_datetime, help="Only include chat logs before this timestamp.")
    parser.add_argument("--limit", type=int, help="Maximum number of rows to export.")
    parser.add_argument(
        "--channels",
        nargs="+",
        help="Only include parsed chat rows with these chat types. Unknown rows are excluded when this is set.",
    )
    parser.add_argument("--output", required=True, help="Output file path.")
    parser.add_argument("--format", choices=("jsonl", "csv"), default="jsonl", help="Output format.")
    return parser.parse_args()


def parse_datetime(value: str) -> datetime:
    try:
        return datetime.fromisoformat(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"Invalid ISO-8601 datetime: {value}") from exc


def load_env_file(path: Path) -> None:
    if not path.exists():
        return

    for raw_line in path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue

        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")

        if key and key not in os.environ:
            os.environ[key] = value


def parse_message(message: str) -> dict[str, Any]:
    for pattern in PATTERNS:
        match = pattern.regex.match(message)
        if match:
            parsed = match.groupdict()
            parsed["chat_type"] = pattern.chat_type
            return parsed

    return {"chat_type": "unknown"}


def build_query(args: argparse.Namespace) -> tuple[Any, dict[str, Any]]:
    conditions = ["al.type = :chat_type"]
    params: dict[str, Any] = {"chat_type": LOG_TYPE_CHAT}

    if args.round_id:
        conditions.append("al.round_id IN :round_ids")
        params["round_ids"] = tuple(args.round_id)

    if args.after:
        conditions.append("al.date >= :after")
        params["after"] = args.after

    if args.before:
        conditions.append("al.date < :before")
        params["before"] = args.before

    limit_clause = ""
    if args.limit is not None:
        limit_clause = "LIMIT :limit"
        params["limit"] = args.limit

    statement = text(f"""
        SELECT
            al.round_id,
            al.admin_log_id AS log_id,
            al.date AS created_at,
            al.message,
            r.start_date AS round_start,
            s.name AS server_name
        FROM admin_log al
        JOIN round r ON r.round_id = al.round_id
        JOIN server s ON s.server_id = r.server_id
        WHERE {" AND ".join(conditions)}
        ORDER BY al.date ASC, al.round_id ASC, al.admin_log_id ASC
        {limit_clause}
    """)

    if args.round_id:
        statement = statement.bindparams(bindparam("round_ids", expanding=True))

    return statement, params


def normalize_row(row: Any) -> dict[str, Any]:
    raw = dict(row)
    parsed = parse_message(raw["message"])

    normalized = {
        "round_id": raw["round_id"],
        "log_id": raw["log_id"],
        "created_at": iso_or_none(raw["created_at"]),
        "round_start": iso_or_none(raw["round_start"]),
        "server_name": raw["server_name"],
        "chat_type": parsed.get("chat_type", "unknown"),
        "sender": parsed.get("sender"),
        "sender_alias": parsed.get("alias"),
        "channel": parsed.get("channel"),
        "target": parsed.get("target"),
        "message": parsed.get("message"),
        "original_message": parsed.get("original"),
        "transformed_message": parsed.get("transformed"),
        "raw_admin_log_message": raw["message"],
    }
    return normalized


def iso_or_none(value: Any) -> str | None:
    if value is None:
        return None
    if isinstance(value, datetime):
        return value.isoformat()
    return str(value)


def ensure_parent(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)


def iter_records(args: argparse.Namespace) -> list[dict[str, Any]]:
    if not args.database_url:
        raise SystemExit("DATABASE_URL is required. Pass --database-url or set DATABASE_URL.")

    engine = create_engine(args.database_url)

    if args.last_rounds is not None and args.last_rounds <= 0:
        raise SystemExit("--last-rounds must be greater than 0.")

    with engine.connect() as conn:
        if args.last_rounds is not None and not args.round_id:
            latest_rounds = conn.execute(
                text(
                    """
                    SELECT round_id
                    FROM round
                    ORDER BY COALESCE(start_date, CURRENT_TIMESTAMP) DESC, round_id DESC
                    LIMIT :limit
                    """
                ),
                {"limit": args.last_rounds},
            ).scalars().all()
            args.round_id = list(reversed(latest_rounds))

        statement, params = build_query(args)
        rows = conn.execute(statement, params).mappings()
        records = [normalize_row(row) for row in rows]

    if args.channels:
        allowed = set(args.channels)
        records = [record for record in records if record["chat_type"] in allowed]

    return records


def write_jsonl(path: Path, records: list[dict[str, Any]]) -> None:
    ensure_parent(path)
    with path.open("w", encoding="utf-8", newline="") as handle:
        for record in records:
            handle.write(json.dumps(record, ensure_ascii=True))
            handle.write("\n")


def write_csv(path: Path, records: list[dict[str, Any]]) -> None:
    ensure_parent(path)
    fieldnames = [
        "round_id",
        "log_id",
        "created_at",
        "round_start",
        "server_name",
        "chat_type",
        "sender",
        "sender_alias",
        "channel",
        "target",
        "message",
        "original_message",
        "transformed_message",
        "raw_admin_log_message",
    ]

    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(records)


def main() -> int:
    args = parse_args()
    load_env_file(Path(args.env_file))

    if not args.database_url:
        args.database_url = os.getenv("DATABASE_URL")

    records = iter_records(args)
    output_path = Path(args.output)

    if args.format == "jsonl":
        write_jsonl(output_path, records)
    else:
        write_csv(output_path, records)

    print(f"Wrote {len(records)} chat records to {output_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
