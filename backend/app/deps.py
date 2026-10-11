"""Request dependencies: DB session, storage, current user, worker auth."""

import hmac
from datetime import timedelta

import jwt
from argon2 import PasswordHasher
from argon2.exceptions import VerificationError
from fastapi import Depends, HTTPException, Request
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy.orm import Session

from .config import Settings
from .db import utcnow
from .models import User

_hasher = PasswordHasher()
_bearer = HTTPBearer(auto_error=False)


def hash_password(password: str) -> str:
    return _hasher.hash(password)


def verify_password(password_hash: str, password: str) -> bool:
    try:
        return _hasher.verify(password_hash, password)
    except VerificationError:
        return False


def get_settings(request: Request) -> Settings:
    return request.app.state.settings


def get_storage(request: Request):
    return request.app.state.storage


def get_db(request: Request):
    db: Session = request.app.state.session_factory()
    try:
        yield db
    finally:
        db.close()


def create_token(user_id: str, settings: Settings) -> str:
    exp = utcnow() + timedelta(days=settings.token_ttl_days)
    return jwt.encode({"sub": user_id, "exp": exp}, settings.secret_key, algorithm="HS256")


def current_user(creds: HTTPAuthorizationCredentials | None = Depends(_bearer), db: Session = Depends(get_db),
                 settings: Settings = Depends(get_settings)) -> User:
    if creds is None:
        raise HTTPException(401, "missing bearer token")
    try:
        user_id = jwt.decode(creds.credentials, settings.secret_key, algorithms=["HS256"])["sub"]
    except jwt.PyJWTError:
        raise HTTPException(401, "invalid or expired token")
    user = db.get(User, user_id)
    if user is None:
        raise HTTPException(401, "user not found")
    return user


def require_worker(creds: HTTPAuthorizationCredentials | None = Depends(_bearer),
                   settings: Settings = Depends(get_settings)) -> None:
    if creds is None or not hmac.compare_digest(creds.credentials, settings.worker_token):
        raise HTTPException(401, "invalid worker token")
