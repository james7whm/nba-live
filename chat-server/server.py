"""Local preview of the NBA Live chat service. Bind to loopback behind HTTPS in production."""
import argparse
import hashlib
import json
import os
import re
import secrets
import sqlite3
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

TEAMS = set('celtics nets knicks 76ers raptors bulls cavaliers pistons pacers bucks hawks hornets heat magic wizards nuggets timberwolves thunder blazers jazz warriors clippers lakers suns kings mavericks rockets grizzlies pelicans spurs'.split())
MAX_BODY = 4096


class ChatStore:
    def __init__(self, path):
        self.path = path
        with self.connect() as db:
            db.executescript('''
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS users (token_hash TEXT PRIMARY KEY, nickname TEXT NOT NULL, avatar INTEGER NOT NULL, team TEXT NOT NULL, last_sent REAL NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room TEXT NOT NULL, nickname TEXT NOT NULL, avatar INTEGER NOT NULL, team TEXT NOT NULL, text TEXT NOT NULL, created INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS messages_room_id ON messages(room, id);
                CREATE TABLE IF NOT EXISTS message_requests (token_hash TEXT NOT NULL, request_id TEXT NOT NULL, room TEXT NOT NULL, text TEXT NOT NULL, message_id INTEGER NOT NULL, PRIMARY KEY(token_hash, request_id));
            ''')

    def connect(self):
        return sqlite3.connect(self.path, timeout=5)

    @staticmethod
    def profile(data):
        name, avatar, team = data.get('nickname'), data.get('avatar'), data.get('team')
        if not isinstance(name, str) or not 1 <= len(name.strip()) <= 12 or any(ord(c) < 32 or ord(c) == 127 for c in name):
            raise ValueError('invalid nickname')
        if type(avatar) is not int or not 0 <= avatar <= 77 or not isinstance(team, str) or (team and team not in TEAMS):
            raise ValueError('invalid profile')
        return name.strip(), avatar, team

    def register(self, data):
        name, avatar, team = self.profile(data)
        token = secrets.token_urlsafe(32)
        with self.connect() as db:
            db.execute('INSERT INTO users(token_hash,nickname,avatar,team) VALUES(?,?,?,?)', (self.digest(token), name, avatar, team))
        return token

    @staticmethod
    def digest(token):
        return hashlib.sha256(token.encode('utf-8')).hexdigest()

    def update(self, token, data):
        profile = self.profile(data)
        with self.connect() as db:
            count = db.execute('UPDATE users SET nickname=?,avatar=?,team=? WHERE token_hash=?', (*profile, self.digest(token))).rowcount
        return count == 1

    def send(self, token, room, text, request_id):
        if not valid_room(room) or not isinstance(text, str) or not 1 <= len(text.strip()) <= 300 or any(ord(c) < 32 and c not in '\n\t' for c in text):
            raise ValueError('invalid message')
        if not isinstance(request_id, str) or re.fullmatch(r'[0-9a-f]{32}', request_id) is None:
            raise ValueError('invalid request id')
        now = time.time()
        with self.connect() as db:
            db.execute('BEGIN IMMEDIATE')
            token_hash = self.digest(token)
            user = db.execute('SELECT nickname,avatar,team,last_sent FROM users WHERE token_hash=?', (token_hash,)).fetchone()
            if not user:
                return 'unauthorized'
            previous = db.execute('SELECT room,text FROM message_requests WHERE token_hash=? AND request_id=?', (token_hash, request_id)).fetchone()
            if previous:
                return 'ok' if previous == (room, text.strip()) else 'conflict'
            if now - user[3] < 2:
                return 'rate_limited'
            db.execute('UPDATE users SET last_sent=? WHERE token_hash=?', (now, token_hash))
            cursor = db.execute('INSERT INTO messages(room,nickname,avatar,team,text,created) VALUES(?,?,?,?,?,?)', (room, user[0], user[1], user[2], text.strip(), int(now)))
            db.execute('INSERT INTO message_requests(token_hash,request_id,room,text,message_id) VALUES(?,?,?,?,?)', (token_hash, request_id, room, text.strip(), cursor.lastrowid))
        return 'ok'

    def read(self, room, after):
        if not valid_room(room) or not isinstance(after, int) or after < 0:
            raise ValueError('invalid cursor')
        with self.connect() as db:
            if after == 0:
                rows = db.execute('SELECT id,nickname,avatar,team,text,created FROM messages WHERE room=? ORDER BY id DESC LIMIT 100', (room,)).fetchall()[::-1]
            else:
                rows = db.execute('SELECT id,nickname,avatar,team,text,created FROM messages WHERE room=? AND id>? ORDER BY id LIMIT 100', (room, after)).fetchall()
        return [dict(zip(('id', 'nickname', 'avatar', 'team', 'text', 'created'), row)) for row in rows]


def valid_room(room):
    return isinstance(room, str) and re.fullmatch(r'[0-9]{1,24}', room) is not None


def make_handler(store):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass  # Never log bearer tokens or message bodies.

        def respond(self, status, body):
            data = json.dumps(body, ensure_ascii=False).encode('utf-8')
            self.send_response(status)
            self.send_header('Content-Type', 'application/json; charset=utf-8')
            self.send_header('Cache-Control', 'no-store')
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def token(self):
            header = self.headers.get('Authorization', '')
            return header[7:] if header.startswith('Bearer ') and len(header) <= 150 else ''

        def body(self):
            length = int(self.headers.get('Content-Length', '0'))
            if not 0 < length <= MAX_BODY:
                raise ValueError('invalid length')
            data = json.loads(self.rfile.read(length))
            if not isinstance(data, dict):
                raise ValueError('invalid body')
            return data

        def do_GET(self):
            try:
                url = urlsplit(self.path)
                if url.path != '/v1/messages':
                    return self.respond(404, {'error': 'not_found'})
                query = parse_qs(url.query)
                messages = store.read(query.get('room', [''])[0], int(query.get('after', ['0'])[0]))
                return self.respond(200, {'messages': messages})
            except (ValueError, TypeError):
                return self.respond(400, {'error': 'invalid_request'})

        def do_POST(self):
            try:
                data = self.body()
                if self.path == '/v1/profile':
                    token = self.token()
                    if token:
                        updated = store.update(token, data)
                        return self.respond(200 if updated else 401, {'ok': True} if updated else {'error': 'unauthorized'})
                    return self.respond(201, {'token': store.register(data)})
                if self.path == '/v1/messages':
                    result = store.send(self.token(), data.get('room'), data.get('text'), data.get('requestId'))
                    return self.respond({'ok': 201, 'unauthorized': 401, 'rate_limited': 429, 'conflict': 409}[result], {'ok': True} if result == 'ok' else {'error': result})
                return self.respond(404, {'error': 'not_found'})
            except (ValueError, TypeError, UnicodeError, json.JSONDecodeError):
                return self.respond(400, {'error': 'invalid_request'})
    return Handler


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--database', default=os.environ.get('NBA_CHAT_DB', 'chat.sqlite3'))
    parser.add_argument('--port', type=int, default=8765)
    args = parser.parse_args()
    server = ThreadingHTTPServer(('127.0.0.1', args.port), make_handler(ChatStore(args.database)))
    server.serve_forever()
