\set ON_ERROR_STOP on
\encoding UTF8
BEGIN;

-- ===== Проєкти =====

CREATE PROCEDURE sp_project_create(p_actor bigint, p_category bigint, p_title text,
    p_description text, p_budget numeric, p_deadline date, INOUT p_id bigint)
LANGUAGE plpgsql AS $$
BEGIN
    IF p_deadline < current_date THEN
        RAISE EXCEPTION 'Строк не може бути в минулому';
    END IF;
    INSERT INTO projects(client_id, category_id, title, description, budget, deadline, updated_by)
    VALUES (p_actor, p_category, p_title, p_description, p_budget, p_deadline, p_actor)
    RETURNING id INTO p_id;
END $$;

CREATE PROCEDURE sp_project_update(p_actor bigint, p_id bigint, p_title text,
    p_description text, p_budget numeric, p_deadline date)
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE projects
    SET title = p_title, description = p_description, budget = p_budget,
        deadline = p_deadline, updated_by = p_actor
    WHERE id = p_id AND client_id = p_actor AND deleted_at IS NULL AND status IN ('draft', 'open');
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Проєкт не знайдено або його не можна редагувати';
    END IF;
END $$;

CREATE PROCEDURE sp_project_set_status(p_actor bigint, p_id bigint, p_status text)
LANGUAGE plpgsql AS $$
DECLARE v_status text;
BEGIN
    SELECT status INTO v_status FROM projects
    WHERE id = p_id AND client_id = p_actor AND deleted_at IS NULL;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Проєкт не знайдено';
    END IF;
    -- дозволені переходи: draft -> open/cancelled, open -> in_progress/cancelled
    IF NOT ((v_status = 'draft' AND p_status IN ('open', 'cancelled'))
         OR (v_status = 'open' AND p_status IN ('in_progress', 'cancelled'))) THEN
        RAISE EXCEPTION 'Неможливо змінити статус з % на %', v_status, p_status;
    END IF;
    -- у роботу можна взяти тільки проєкт з контрактом, етапи якого покривають всю суму
    IF p_status = 'in_progress' AND NOT EXISTS (
        SELECT 1 FROM contracts c
        WHERE c.project_id = p_id
          AND c.agreed_amount = (SELECT coalesce(sum(amount), 0) FROM milestones WHERE contract_id = c.id)) THEN
        RAISE EXCEPTION 'Сума етапів має дорівнювати сумі контракту';
    END IF;
    UPDATE projects SET status = p_status, updated_by = p_actor WHERE id = p_id;
END $$;

CREATE PROCEDURE sp_project_soft_delete(p_actor bigint, p_id bigint)
LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM contracts WHERE project_id = p_id AND status = 'active') THEN
        RAISE EXCEPTION 'Не можна видалити проєкт з активним контрактом';
    END IF;
    UPDATE projects SET deleted_at = now(), deleted_by = p_actor, updated_by = p_actor
    WHERE id = p_id AND client_id = p_actor AND deleted_at IS NULL;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Проєкт не знайдено';
    END IF;
END $$;

CREATE PROCEDURE sp_project_restore(p_actor bigint, p_id bigint)
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE projects SET deleted_at = NULL, deleted_by = NULL, updated_by = p_actor
    WHERE id = p_id AND client_id = p_actor AND deleted_at IS NOT NULL;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Видалений проєкт не знайдено';
    END IF;
END $$;

-- ===== Заявки =====

CREATE PROCEDURE sp_proposal_create(p_actor bigint, p_project bigint, p_letter text,
    p_amount numeric, p_days integer, INOUT p_id bigint)
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM projects WHERE id = p_project AND status = 'open'
                   AND deleted_at IS NULL AND client_id <> p_actor) THEN
        RAISE EXCEPTION 'На цей проєкт не можна подати заявку';
    END IF;
    INSERT INTO proposals(project_id, freelancer_id, cover_letter, proposed_amount, estimated_days, updated_by)
    VALUES (p_project, p_actor, p_letter, p_amount, p_days, p_actor)
    RETURNING id INTO p_id;
END $$;

CREATE PROCEDURE sp_proposal_accept(p_actor bigint, p_id bigint)
LANGUAGE plpgsql AS $$
DECLARE v_project bigint;
BEGIN
    SELECT p.project_id INTO v_project FROM proposals p
    JOIN projects pr ON pr.id = p.project_id
    WHERE p.id = p_id AND p.status = 'pending' AND pr.client_id = p_actor AND pr.status = 'open';
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Заявку не можна прийняти';
    END IF;
    UPDATE proposals SET status = 'accepted', updated_by = p_actor WHERE id = p_id;
    -- решта заявок на цей проєкт відхиляються
    UPDATE proposals SET status = 'rejected', updated_by = p_actor
    WHERE project_id = v_project AND id <> p_id AND status = 'pending';
END $$;

CREATE PROCEDURE sp_proposal_withdraw(p_actor bigint, p_id bigint)
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE proposals SET status = 'withdrawn', updated_by = p_actor
    WHERE id = p_id AND freelancer_id = p_actor AND status = 'pending';
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Заявку не можна відкликати';
    END IF;
END $$;

-- ===== Контракти та етапи =====

CREATE PROCEDURE sp_contract_create(p_actor bigint, p_proposal bigint, INOUT p_id bigint)
LANGUAGE plpgsql AS $$
DECLARE v_project bigint; v_amount numeric;
BEGIN
    SELECT p.project_id, p.proposed_amount INTO v_project, v_amount FROM proposals p
    JOIN projects pr ON pr.id = p.project_id
    WHERE p.id = p_proposal AND p.status = 'accepted' AND pr.client_id = p_actor;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Потрібна прийнята заявка на ваш проєкт';
    END IF;
    INSERT INTO contracts(project_id, proposal_id, agreed_amount, updated_by)
    VALUES (v_project, p_proposal, v_amount, p_actor)
    RETURNING id INTO p_id;
END $$;

CREATE PROCEDURE sp_milestone_create(p_actor bigint, p_contract bigint, p_title text,
    p_amount numeric, p_due date, INOUT p_id bigint)
LANGUAGE plpgsql AS $$
DECLARE v_amount numeric;
BEGIN
    SELECT c.agreed_amount INTO v_amount FROM contracts c
    JOIN projects pr ON pr.id = c.project_id
    WHERE c.id = p_contract AND c.status = 'active' AND pr.client_id = p_actor;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Контракт не знайдено';
    END IF;
    IF p_amount <= 0 THEN
        RAISE EXCEPTION 'Сума етапу має бути більшою за 0';
    END IF;
    IF p_amount + coalesce((SELECT sum(amount) FROM milestones WHERE contract_id = p_contract), 0) > v_amount THEN
        RAISE EXCEPTION 'Сума етапів перевищує суму контракту';
    END IF;
    INSERT INTO milestones(contract_id, title, amount, due_date)
    VALUES (p_contract, p_title, p_amount, p_due)
    RETURNING id INTO p_id;
END $$;

CREATE PROCEDURE sp_submission_create(p_actor bigint, p_milestone bigint,
    p_description text, p_url text, INOUT p_id bigint)
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM milestones m
                   JOIN contracts c ON c.id = m.contract_id
                   JOIN proposals p ON p.id = c.proposal_id
                   WHERE m.id = p_milestone AND p.freelancer_id = p_actor AND m.status <> 'approved') THEN
        RAISE EXCEPTION 'Етап не знайдено';
    END IF;
    INSERT INTO submissions(milestone_id, description, result_url)
    VALUES (p_milestone, p_description, p_url)
    RETURNING id INTO p_id;
    UPDATE milestones SET status = 'submitted' WHERE id = p_milestone;
END $$;

CREATE PROCEDURE sp_milestone_approve(p_actor bigint, p_id bigint)
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE milestones m SET status = 'approved'
    FROM contracts c JOIN projects pr ON pr.id = c.project_id
    WHERE m.id = p_id AND c.id = m.contract_id AND pr.client_id = p_actor AND m.status = 'submitted';
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Етап ще не здано або не знайдено';
    END IF;
END $$;

CREATE PROCEDURE sp_payment_register(p_actor bigint, p_milestone bigint, p_amount numeric,
    p_reference text, INOUT p_id bigint)
LANGUAGE plpgsql AS $$
DECLARE v_amount numeric;
BEGIN
    SELECT m.amount INTO v_amount FROM milestones m
    JOIN contracts c ON c.id = m.contract_id
    JOIN projects pr ON pr.id = c.project_id
    WHERE m.id = p_milestone AND m.status = 'approved' AND pr.client_id = p_actor;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Оплатити можна лише підтверджений етап';
    END IF;
    IF p_amount + coalesce((SELECT sum(amount) FROM payments WHERE milestone_id = p_milestone), 0) > v_amount THEN
        RAISE EXCEPTION 'Сума оплати більша за залишок етапу';
    END IF;
    INSERT INTO payments(milestone_id, amount, external_reference)
    VALUES (p_milestone, p_amount, p_reference)
    RETURNING id INTO p_id;
END $$;

CREATE PROCEDURE sp_contract_complete(p_actor bigint, p_id bigint)
LANGUAGE plpgsql AS $$
DECLARE v_project bigint;
BEGIN
    SELECT c.project_id INTO v_project FROM contracts c
    JOIN projects pr ON pr.id = c.project_id
    WHERE c.id = p_id AND c.status = 'active' AND pr.client_id = p_actor;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Активний контракт не знайдено';
    END IF;
    IF EXISTS (SELECT 1 FROM milestones WHERE contract_id = p_id AND status <> 'approved')
       OR fn_contract_outstanding(p_id) <> 0 THEN
        RAISE EXCEPTION 'Не всі етапи підтверджені або оплачені';
    END IF;
    UPDATE contracts SET status = 'completed', updated_by = p_actor WHERE id = p_id;
    UPDATE projects SET status = 'completed', updated_by = p_actor WHERE id = v_project;
END $$;

-- ===== Відгуки та повідомлення =====

CREATE PROCEDURE sp_review_create(p_actor bigint, p_contract bigint,
    p_rating smallint, p_comment text, INOUT p_id bigint)
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM contracts c
                   JOIN projects pr ON pr.id = c.project_id
                   JOIN proposals p ON p.id = c.proposal_id
                   WHERE c.id = p_contract AND c.status = 'completed'
                     AND p_actor IN (pr.client_id, p.freelancer_id)) THEN
        RAISE EXCEPTION 'Відгук можна залишити лише до свого завершеного контракту';
    END IF;
    INSERT INTO reviews(contract_id, author_id, rating, comment)
    VALUES (p_contract, p_actor, p_rating, p_comment)
    RETURNING id INTO p_id;
END $$;

CREATE PROCEDURE sp_message_send(p_actor bigint, p_contract bigint, p_recipient bigint,
    p_body text, INOUT p_id bigint)
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM contracts c
                   JOIN projects pr ON pr.id = c.project_id
                   JOIN proposals p ON p.id = c.proposal_id
                   WHERE c.id = p_contract
                     AND p_actor IN (pr.client_id, p.freelancer_id)
                     AND p_recipient IN (pr.client_id, p.freelancer_id)) THEN
        RAISE EXCEPTION 'Писати можуть лише учасники контракту';
    END IF;
    INSERT INTO messages(contract_id, sender_id, recipient_id, body)
    VALUES (p_contract, p_actor, p_recipient, p_body)
    RETURNING id INTO p_id;
END $$;

-- ===== Портфоліо =====

CREATE PROCEDURE sp_portfolio_create(p_actor bigint, p_title text, p_description text,
    p_url text, INOUT p_id bigint)
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO portfolio_items(freelancer_id, title, description, project_url, updated_by)
    VALUES (p_actor, p_title, p_description, p_url, p_actor)
    RETURNING id INTO p_id;
END $$;

CREATE PROCEDURE sp_portfolio_soft_delete(p_actor bigint, p_id bigint)
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE portfolio_items SET deleted_at = now(), deleted_by = p_actor, updated_by = p_actor
    WHERE id = p_id AND freelancer_id = p_actor AND deleted_at IS NULL;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Роботу не знайдено';
    END IF;
END $$;

CREATE PROCEDURE sp_portfolio_restore(p_actor bigint, p_id bigint)
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE portfolio_items SET deleted_at = NULL, deleted_by = NULL, updated_by = p_actor
    WHERE id = p_id AND freelancer_id = p_actor AND deleted_at IS NOT NULL;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Видалену роботу не знайдено';
    END IF;
END $$;
COMMIT;
