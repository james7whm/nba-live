"""Exercise the actual WPF picker against an isolated local chat server."""
import os
import pathlib
import subprocess
import sys
import tempfile
import threading
from http.server import ThreadingHTTPServer

root = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(root / 'chat-server'))
from server import ChatStore, make_handler

with tempfile.TemporaryDirectory(prefix='nba-chat-ui-') as temporary:
    server = ThreadingHTTPServer(('127.0.0.1', 0), make_handler(ChatStore(str(pathlib.Path(temporary) / 'chat.sqlite3'))))
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        endpoint = 'http://127.0.0.1:' + str(server.server_port)
        environment = os.environ.copy()
        environment['NBALIVE_CHAT_ENDPOINT'] = endpoint
        environment['NBALIVE_CHAT_TEST_ENDPOINT'] = endpoint
        command = [str(root / '.tools/dotnet/dotnet.exe'), str(root / 'artifacts/chat-emoji-redesign-test/NbaLive.UiTests.dll'), '--chat-only']
        subprocess.run(command, cwd=root, env=environment, check=True, timeout=90)
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=3)
