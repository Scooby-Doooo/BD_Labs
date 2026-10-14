\set ON_ERROR_STOP on
\encoding UTF8
BEGIN;

-- ===== Представлення (View) =====

-- Проєкти без видалених (soft delete)
CREATE VIEW vw_active_projects AS
SELECT p.id, p.client_id, u.display_name AS client_name, c.name AS category,
       p.title, p.description, p.budget, p.deadline, p.status,
       p.updated_at, p.updated_by, p.search_vector
FROM projects p
JOIN users u ON u.id = p.client_id
JOIN categories c ON c.id = p.category_id
WHERE p.deleted_at IS NULL;

-- Фрілансери з рейтингом
CREATE VIEW vw_freelancers AS
SELECT f.user_id, u.display_name, f.title, f.hourly_rate,
       fn_freelancer_rating(f.user_id) AS rating
FROM freelancer_profiles f
JOIN users u ON u.id = f.user_id;

-- Заявки з іменем фрілансера (тільки для не видалених проєктів)
CREATE VIEW vw_project_proposals AS
SELECT p.id, p.project_id, p.freelancer_id, u.display_name AS freelancer_name,
       p.proposed_amount, p.estimated_days, p.status, p.updated_at, p.updated_by
FROM proposals p
JOIN users u ON u.id = p.freelancer_id
JOIN projects pr ON pr.id = p.project_id
WHERE pr.deleted_at IS NULL;

-- Контракти зі сплаченою сумою та залишком
CREATE VIEW vw_contract_summary AS
SELECT c.id, c.project_id, pr.title AS project_title, pr.client_id, p.freelancer_id,
       c.agreed_amount, c.status,
       c.agreed_amount - fn_contract_outstanding(c.id) AS paid_amount,
       fn_contract_outstanding(c.id) AS outstanding_amount
FROM contracts c
JOIN projects pr ON pr.id = c.project_id
JOIN proposals p ON p.id = c.proposal_id;

-- Етапи зі сплаченою сумою
CREATE VIEW vw_milestones AS
SELECT m.id, m.contract_id, m.title, m.amount, m.due_date, m.status,
       coalesce((SELECT sum(pay.amount) FROM payments pay WHERE pay.milestone_id = m.id), 0) AS paid_amount
FROM milestones m;

-- Портфоліо без видалених робіт
CREATE VIEW vw_portfolio AS
SELECT id, freelancer_id, title, description, project_url, updated_at, updated_by
FROM portfolio_items
WHERE deleted_at IS NULL;

CREATE VIEW vw_users AS SELECT id, display_name FROM users;
CREATE VIEW vw_reviews AS SELECT id, contract_id, author_id, rating, comment FROM reviews;
CREATE VIEW vw_messages AS SELECT id, contract_id, sender_id, recipient_id, body, sent_at FROM messages;
CREATE VIEW vw_submissions AS SELECT id, milestone_id, description, result_url, submitted_at FROM submissions;

-- ===== Індекси =====

-- B-tree: заявки проєкту за статусом
CREATE INDEX ix_proposals_project_status ON proposals USING btree (project_id, status);
-- B-tree (частковий): проєкти замовника, тільки не видалені
CREATE INDEX ix_projects_client ON projects USING btree (client_id) WHERE deleted_at IS NULL;
-- GIN: повнотекстовий пошук по назві й опису проєкту
-- (fastupdate = off - нові рядки одразу записуються в індекс)
CREATE INDEX ix_projects_search ON projects USING gin (search_vector) WITH (fastupdate = off);
-- Унікальний частковий індекс: у проєкту може бути лише одна прийнята заявка
CREATE UNIQUE INDEX ux_proposals_one_accepted ON proposals (project_id) WHERE status = 'accepted';
-- Індекси для зовнішніх ключів
CREATE INDEX ix_milestones_contract ON milestones (contract_id);
CREATE INDEX ix_payments_milestone ON payments (milestone_id);
CREATE INDEX ix_messages_contract ON messages (contract_id);
COMMIT;
