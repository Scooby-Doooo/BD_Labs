\set ON_ERROR_STOP on
\encoding UTF8
BEGIN;

-- Тригер: при кожній зміні рядка оновлюємо дату останньої зміни.
-- Користувача (updated_by) передають процедури.
CREATE FUNCTION fn_set_updated_at() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END $$;

CREATE TRIGGER trg_projects_updated BEFORE UPDATE ON projects FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();
CREATE TRIGGER trg_proposals_updated BEFORE UPDATE ON proposals FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();
CREATE TRIGGER trg_contracts_updated BEFORE UPDATE ON contracts FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();
CREATE TRIGGER trg_portfolio_updated BEFORE UPDATE ON portfolio_items FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();

-- Тригер: таблиці з soft delete не можна видаляти фізично.
CREATE FUNCTION fn_forbid_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Фізичне видалення з % заборонене, використовуйте soft delete', TG_TABLE_NAME;
END $$;

CREATE TRIGGER trg_projects_no_delete BEFORE DELETE ON projects FOR EACH ROW EXECUTE FUNCTION fn_forbid_delete();
CREATE TRIGGER trg_portfolio_no_delete BEFORE DELETE ON portfolio_items FOR EACH ROW EXECUTE FUNCTION fn_forbid_delete();

-- Середня оцінка фрілансера від замовників.
CREATE FUNCTION fn_freelancer_rating(p_freelancer bigint) RETURNS numeric LANGUAGE sql AS $$
    SELECT round(avg(r.rating), 2)
    FROM reviews r
    JOIN contracts c ON c.id = r.contract_id
    JOIN proposals p ON p.id = c.proposal_id
    JOIN projects pr ON pr.id = c.project_id
    WHERE p.freelancer_id = p_freelancer AND r.author_id = pr.client_id
$$;

-- Скільки ще залишилось сплатити за контрактом.
CREATE FUNCTION fn_contract_outstanding(p_contract bigint) RETURNS numeric LANGUAGE sql AS $$
    SELECT c.agreed_amount - coalesce((
        SELECT sum(pay.amount) FROM payments pay
        JOIN milestones m ON m.id = pay.milestone_id
        WHERE m.contract_id = c.id), 0)
    FROM contracts c WHERE c.id = p_contract
$$;
COMMIT;
