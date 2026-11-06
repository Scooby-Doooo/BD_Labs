# Базові запити Redis у предметній області платформи для фрілансерів.
r() { echo "> $*"; redis-cli "$@"; }
redis-cli FLUSHDB > /dev/null

echo; echo "--- STRING: кеш картки проєкту з часом життя 60 с"
r SET project:42:card '{"id":42,"title":"Backend API","budget":12000}' EX 60
r GET project:42:card
r TTL project:42:card
r EXISTS project:42:card
r DEL project:42:card
r GET project:42:card

echo; echo "--- Лічильник переглядів: INCR атомарний"
r INCR project:42:views
r INCRBY project:42:views 10
r GET project:42:views

echo; echo "--- Обмеження частоти: лічильник заявок фрілансера за годину"
r INCR rate:proposals:freelancer:7
r EXPIRE rate:proposals:freelancer:7 3600
r INCR rate:proposals:freelancer:7
r INCR rate:proposals:freelancer:7
r TTL rate:proposals:freelancer:7

echo; echo "--- HASH: сесія користувача"
r HSET session:abc user_id 7 role freelancer lang uk
r HGET session:abc role
r HSET session:abc lang en
r HGETALL session:abc
r EXPIRE session:abc 1800

echo; echo "--- SET: хто зараз онлайн, перетин двох множин"
r SADD online 7 8 9 10
r SADD skill:postgresql 7 9 11
r SISMEMBER online 7
r SINTER online skill:postgresql
r SREM online 9
r SCARD online

echo; echo "--- LIST: останні переглянуті проєкти, зберігаємо 3"
r LPUSH viewed:7 101
r LPUSH viewed:7 102
r LPUSH viewed:7 103
r LPUSH viewed:7 104
r LTRIM viewed:7 0 2
r LRANGE viewed:7 0 -1

echo; echo "--- ZSET: рейтинг фрілансерів за заробленою сумою"
r ZADD leaderboard 12000 freelancer:7 8500 freelancer:8 21000 freelancer:9
r ZINCRBY leaderboard 10000 freelancer:8
r ZREVRANGE leaderboard 0 1 WITHSCORES
r ZREVRANK leaderboard freelancer:7
r ZSCORE leaderboard freelancer:8
r ZRANGEBYSCORE leaderboard 15000 25000 WITHSCORES

echo; echo "--- MULTI/EXEC: заявка і рейтинг змінюються атомарно"
printf 'MULTI\nINCR project:42:proposals\nZADD leaderboard 1 freelancer:12\nEXEC\n' | redis-cli

echo; echo "--- SCAN: пошук ключів за шаблоном без блокування сервера"
r SCAN 0 MATCH 'project:*' COUNT 100
