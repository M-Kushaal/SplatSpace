from datetime import datetime, timezone
from pathlib import Path

from sqlalchemy import create_engine
from sqlalchemy.orm import DeclarativeBase, sessionmaker


class Base(DeclarativeBase):
    pass


def utcnow() -> datetime:
    """Naive UTC, so SQLite and Postgres compare timestamps the same way."""
    return datetime.now(timezone.utc).replace(tzinfo=None)


def make_session_factory(database_url: str) -> sessionmaker:
    connect_args = {}
    if database_url.startswith("sqlite"):
        connect_args["check_same_thread"] = False
        db_file = database_url.split("///", 1)[-1]
        if db_file and db_file != ":memory:":
            Path(db_file).parent.mkdir(parents=True, exist_ok=True)
    engine = create_engine(database_url, connect_args=connect_args)
    from . import models  # noqa: F401 - register tables

    Base.metadata.create_all(engine)
    return sessionmaker(bind=engine, expire_on_commit=False)
