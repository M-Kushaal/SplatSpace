import httpx

from gs_worker.runner import Worker


class FlakyClient:
    """Fails the first `failures` POSTs with the error seen on Windows, then answers 'no job'."""

    def __init__(self, failures):
        self.failures, self.calls = failures, 0

    def post(self, url, json=None, headers=None):
        self.calls += 1
        if self.calls <= self.failures:
            raise httpx.ReadError("[WinError 10053] An established connection was aborted")
        return httpx.Response(200, content=b"null", request=httpx.Request("POST", url))  # server: no job


def test_transient_connection_error_is_retried(monkeypatch):
    monkeypatch.setattr("time.sleep", lambda s: None)
    client = FlakyClient(failures=2)
    assert Worker("http://x", "t", client=client).run(once=True) == 0
    assert client.calls == 3


def test_worker_survives_server_outage(monkeypatch):
    monkeypatch.setattr("time.sleep", lambda s: None)
    client = FlakyClient(failures=10)
    assert Worker("http://x", "t", client=client).run(once=True) == 0  # returns instead of crashing
