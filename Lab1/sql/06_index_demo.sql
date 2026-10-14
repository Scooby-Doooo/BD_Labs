\set ON_ERROR_STOP on
\encoding UTF8
-- Демонстрація використання індексів.
-- Додаємо 10 000 тестових проєктів, дивимось плани запитів і відкочуємо зміни.
BEGIN;
INSERT INTO projects(client_id, category_id, title, description, budget, deadline, updated_by)
SELECT CASE WHEN i % 100 = 0 THEN 4 ELSE 1 END, 1, 'Test project ' || i,
       CASE WHEN i % 500 = 0 THEN 'rareword' ELSE 'ordinary description' END,
       100, current_date + 60, 1
FROM generate_series(1, 10000) AS s(i);
ANALYZE projects;

\echo 'GIN: повнотекстовий пошук'
EXPLAIN (ANALYZE, COSTS OFF, BUFFERS OFF)
SELECT id, title FROM projects
WHERE search_vector @@ plainto_tsquery('simple', 'rareword');

\echo 'B-tree: проєкти замовника'
EXPLAIN (ANALYZE, COSTS OFF, BUFFERS OFF)
SELECT id, title FROM projects WHERE client_id = 4 AND deleted_at IS NULL;
ROLLBACK;
