import tempfile
import unittest
import json
import threading
import urllib.request
import uuid
from pathlib import Path
from http.server import ThreadingHTTPServer
from server import ChatStore, make_handler


class ChatTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = ChatStore(str(Path(self.temp.name) / 'chat.sqlite3'))

    def tearDown(self):
        self.temp.cleanup()

    def test_two_users_isolated_rooms_and_cursor(self):
        first = self.store.register({'nickname': '球迷', 'avatar': 1, 'team': 'knicks'})
        other = self.store.register({'nickname': '路人', 'avatar': 0, 'team': ''})
        first_id = uuid.uuid4().hex
        self.assertEqual(self.store.send(first, '123', '好球', first_id), 'ok')
        self.assertEqual(self.store.send(first, '123', '好球', first_id), 'ok')
        self.assertEqual(self.store.send(first, '123', '不同内容', first_id), 'conflict')
        self.assertEqual(self.store.send(other, '123', '精彩', uuid.uuid4().hex), 'ok')
        rows = self.store.read('123', 0)
        self.assertEqual([m['text'] for m in rows], ['好球', '精彩'])
        self.assertEqual(rows[1]['team'], '')
        self.assertEqual([m['text'] for m in self.store.read('123', rows[0]['id'])], ['精彩'])
        self.assertEqual(self.store.read('124', 0), [])
        self.assertEqual(self.store.send(first, '123', '刷屏', uuid.uuid4().hex), 'rate_limited')
        self.assertEqual(self.store.send('bad-token', '123', '伪造', uuid.uuid4().hex), 'unauthorized')

    def test_validation_and_profile(self):
        with self.assertRaises(ValueError):
            self.store.register({'nickname': 'x' * 13, 'avatar': 0, 'team': ''})
        token = self.store.register({'nickname': '旧昵称', 'avatar': 0, 'team': ''})
        self.assertTrue(self.store.update(token, {'nickname': '新昵称', 'avatar': 72, 'team': 'lakers'}))
        self.assertEqual(self.store.send(token, '999', '你好', uuid.uuid4().hex), 'ok')
        self.assertEqual(self.store.read('999', 0)[0]['nickname'], '新昵称')
        self.assertEqual(self.store.read('999', 0)[0]['avatar'], 72)
        with self.assertRaises(ValueError):
            self.store.update(token, {'nickname': '球迷', 'avatar': 78, 'team': ''})
        with self.assertRaises(ValueError):
            self.store.read('../other', 0)
        with self.assertRaises(ValueError):
            self.store.send(token, '999', 'x' * 301, uuid.uuid4().hex)
        with self.assertRaises(ValueError):
            self.store.send(token, '999', '你好', 'invalid')

    def test_http_two_clients(self):
        server = ThreadingHTTPServer(('127.0.0.1', 0), make_handler(self.store))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        base = 'http://127.0.0.1:' + str(server.server_port)
        def post(path, body, token=None):
            headers = {'Content-Type': 'application/json'}
            if token:
                headers['Authorization'] = 'Bearer ' + token
            req = urllib.request.Request(base + path, data=json.dumps(body).encode(), headers=headers)
            with urllib.request.urlopen(req, timeout=3) as response:
                return json.load(response)
        try:
            one = post('/v1/profile', {'nickname': '小林', 'avatar': 0, 'team': ''})['token']
            two = post('/v1/profile', {'nickname': '阿七', 'avatar': 1, 'team': 'knicks'})['token']
            post('/v1/messages', {'room': '168875', 'text': '你好', 'requestId': uuid.uuid4().hex}, one)
            post('/v1/messages', {'room': '168875', 'text': '好球', 'requestId': uuid.uuid4().hex}, two)
            with urllib.request.urlopen(base + '/v1/messages?room=168875&after=0', timeout=3) as response:
                messages = json.load(response)['messages']
            self.assertEqual([m['text'] for m in messages], ['你好', '好球'])
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=3)


if __name__ == '__main__':
    unittest.main()
